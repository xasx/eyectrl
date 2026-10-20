namespace EyeCtrl.App.Services.Coordinators;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;   // UIConstants (edge-bar anchor radius)
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the keyboard lifecycle surface of <see cref="EventCoordinator"/>: keyboard show/hide
/// (<see cref="SetKeyboardVisible"/>, the toggle/show/hide request messages), layout loading
/// (<see cref="LoadKeyboard"/>), the emoji skin-tone handler, and the snapshot-driven reload seam
/// (<see cref="ReloadIfChanged"/>, consumed by SettingsUiCoordinator). Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition (cluster 3).
/// </summary>
internal sealed class KeyboardCoordinator : CoordinatorBase
{
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    // Constructed synchronously so the property seams below work before Initialize; OnInitialize
    // replaces it with the background-mode instance (dispatcher-marshaled swaps). Not readonly
    // for exactly that replacement.
    private PredictionStackFactory _predictionFactory = new();
    private KeyboardStackSnapshot? _keyboardSnapshot;
    // Set when ShowKeyboardEdgeBar had to skip because another context owned the shared bar;
    // cleared when the pair is (re)shown or the keyboard hides. Drives the B4 #10 retry.
    private bool _keyboardPairWanted;
    // The bar VM the ButtonSetChanged retry subscription is attached to (attached lazily on
    // the first keyboard show — the VM only exists once OverlayManager.Initialize has run).
    private EdgeButtonBarViewModel? _hookedEdgeBarVm;
    // Owned HERE — moved from EventCoordinator.Initialize. Used by LoadKeyboard's
    // recent-emoji path and as the model-dir root for PredictionStackFactory.Rebuild.
    private string? _appDataDir;
    // SetModelServices hands only the registry to PredictionStackFactory.Rebuild; the download
    // service is dead and dropped (see EC.SetModelServices, kept 3-arg for App compatibility).
    private ModelRegistryService? _modelRegistry;
    public WordPredictor? WordPredictor => _predictionFactory.Current?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _predictionFactory.Current;

    public KeyboardCoordinator(ModalInteractionState modal,
        QuickActionsCoordinator quickActions, ToolbarLifecycleCoordinator toolbar)
    {
        // _modal is read by ApplyModalKeyboardState; _quickActions by the same; _toolbar by the
        // yield subscription in OnInitialize (ToolbarVisibilityChanged). All three are live
        // dependencies, so reject nulls at the ctor rather than at first keyboard toggle.
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _quickActions = quickActions ?? throw new ArgumentNullException(nameof(quickActions));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
    }

    protected override void OnInitialize()
    {
        var context = Context!;
        _appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EyeCtrl");

        var messenger = context.Messenger;

        // Keyboard toggle
        messenger.Register<KeyboardCoordinator, KeyboardToggledMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(!_modal.KeyboardVisible, manageSuppression: true)));

        // Ring / edge-bar show: idempotent (never hides), with the toolbar toggle's
        // suppression semantics — a show that leaves free-look active would let the next
        // fixation pop an action menu over the keyboard.
        messenger.Register<KeyboardCoordinator, KeyboardShowRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() =>
            {
                if (ShouldShow(_modal.KeyboardVisible))
                    SetKeyboardVisible(true, manageSuppression: true);
            }));

        // Edge-bar Close: the keyboard's own escape — hides AND releases suppression so the
        // ring comes back (spec ledger #13).
        messenger.Register<KeyboardCoordinator, KeyboardHideRequestedMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(false, manageSuppression: true)));

        // ShowKeyboardRequestMessage -> show keyboard for text input
        messenger.Register<KeyboardCoordinator, ShowKeyboardRequestMessage>(this, (_, msg) =>
        {
            if (context?.OverlayManager.Keyboard == null) return;
            context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(true, manageSuppression: false));
            Log.Debug("Keyboard shown for text input (target handle: {Handle})", msg.TargetHandle);
        });

        // HideKeyboardRequestMessage -> hide keyboard
        messenger.Register<KeyboardCoordinator, HideKeyboardRequestMessage>(this, (_, _) =>
        {
            if (context?.OverlayManager.Keyboard == null) return;
            context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(false, manageSuppression: false));
            Log.Debug("Keyboard hidden");
        });

        // Edge-bar Flip (spec B1/B4): flip, apply to the window, persist, refresh the
        // snapshot, and rebuild the bar at the keyboard's new edge (ledger #15).
        messenger.Register<KeyboardCoordinator, KeyboardEdgeToggledMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(HandleKeyboardEdgeToggled));

        // Yield wiring (spec B3 #7): react to real toolbar transitions, and on keyboard show
        // query the window directly — the startup show bypasses the adapter, so a tracked
        // adapter flag can read "not occupied" while the toolbar is on screen.
        _toolbar.ToolbarVisibilityChanged += OnToolbarVisibilityChanged;

        context.OverlayManager.Keyboard.SkinToneChanged += OnEmojiSkinToneChanged;

        // Background-mode prediction stack: swaps land on THIS thread and re-bind the keyboard
        // here. Building the stack is multi-second work (dictionaries, trie, ONNX session) that
        // used to run inside LoadKeyboard — freezing the boot splash and, worse, freezing all
        // gaze UI on every mid-session settings apply. Rebuild now returns the OLD stack (or
        // null on first boot) immediately; the swap callback below installs the new one.
        _predictionFactory.Dispose();
        _predictionFactory = new PredictionStackFactory(work => context.Dispatcher.BeginInvoke(work));
        _predictionFactory.StackSwapped += OnStackSwapped;
        // Hand the fresh factory to the dictionary page's service (SetModelServices pattern):
        // its ForgetWords needs the LIVE stack so a settings-page delete takes effect at once
        // and survives the in-flight-rebuild window.
        context.UserDictionary?.SetFactory(_predictionFactory);
    }

    /// <summary>Stores the model registry for the next <see cref="LoadKeyboard"/> rebuild. Keyboard
    /// only needs the registry; EC fans <c>management</c> to SettingsUi separately. EC's public
    /// <c>SetModelServices</c> keeps its 3-arg signature because App.xaml.cs calls it that way.</summary>
    public void SetModelServices(ModelRegistryService registry) => _modelRegistry = registry;

    // 4 args (appDataDir is the field). Body moved verbatim from EventCoordinator.LoadKeyboard,
    // INCLUDING the _appDataDir InvalidOperationException guard (which moved here with the field).
    public void LoadKeyboard(KeyboardWindow keyboard, EyeCtrlSettings settings,
        IInputSimulator inputSimulator, SettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(keyboard);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(inputSimulator);
        ArgumentNullException.ThrowIfNull(settingsService);

        if (string.IsNullOrWhiteSpace(_appDataDir))
            throw new InvalidOperationException("KeyboardCoordinator must be initialized before calling LoadKeyboard.");

        var layoutName = settings.Keyboard.ActiveLayout.ToLowerInvariant();
        var layoutPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            "Resources", "Layouts", $"{layoutName}.json");

        if (File.Exists(layoutPath))
        {
            // A corrupt layout or emoji file must degrade the keyboard, not kill boot. The
            // exception used to escape into App.OnStartup's catch, whose failure path ends in
            // Shutdown() — the whole app (cursor included) dead over one resource file, with a
            // modal box the user may be unable to dismiss. Emoji loading is skipped when the
            // layout failed: SetEmojiData assumes a loaded keyboard.
            var layoutLoaded = false;
            try
            {
                var layout = KeyboardLayoutLoader.Load(layoutPath);
                keyboard.LoadLayout(layout,
                    settings.Keyboard.KeySizeMultiplier,
                    settings.Dwell.KeyboardKeyDwellMs,
                    settings.Dwell.KeyboardPredictionDwellMs,
                    settings.Dwell.DwellCooldownMs);
                layoutLoaded = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Keyboard layout {Path} failed to load; the keyboard is unavailable this session", layoutPath);
            }

            if (layoutLoaded)
            {
                try
                {
                    var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
                    var recentPath = Path.Combine(_appDataDir!, "recent_emojis.json");
                    keyboard.SetEmojiData(emojiPath, recentPath);
                    keyboard.SetSkinTone(settings.Keyboard.EmojiSkinTone);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Emoji data failed to load; the emoji panel is unavailable this session");
                }
            }
        }

        if (settings.Keyboard.PredictionEnabled && string.IsNullOrEmpty(settings.Keyboard.LanguagePack))
        {
            settings.Keyboard.LanguagePack = LanguageDetector.Detect();
            settingsService.SaveDebounced(settings);
            Log.Information("Auto-detected language: {Lang}", settings.Keyboard.LanguagePack);
        }

        var orchestrator = _predictionFactory.Rebuild(settings, AppDomain.CurrentDomain.BaseDirectory, _appDataDir!, _modelRegistry);
        keyboard.SetOrchestrator(orchestrator);

        keyboard.SetScreenEdge(settings.Keyboard.ScreenEdge);
        keyboard.SetSettings(settings);

        var wavPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "click.wav");
        keyboard.SetSoundService(new KeyboardSoundService(settings.Keyboard, wavPath));

        _keyboardSnapshot = CaptureSnapshot(settings);
    }

    /// <summary>Seam consumed by SettingsUiCoordinator (T7): reload the keyboard iff the captured
    /// stack inputs changed, so T7 doesn't read <c>_keyboardSnapshot</c>. Returns <c>true</c> if
    /// reloaded. The inputs include the snapshot's filesystem term (<c>ActiveModelOnDisk</c>), so
    /// a completed download of the model <c>MLModelSize</c> already names — which changes no
    /// setting — still rebuilds the prediction stack (ticket 11).</summary>
    internal bool ReloadIfChanged(EyeCtrlSettings settings, IInputSimulator input, SettingsService ss)
    {
        var snap = CaptureSnapshot(settings);
        if (!ShouldReload(_keyboardSnapshot, snap)) return false;
        if (Context is null) return false; // defensive — the seam is only reachable post-Initialize
        LoadKeyboard(Context.OverlayManager.Keyboard, settings, input, ss);
        return true;
    }

    /// <summary>
    /// The stored and comparison captures of the reload seam, collapsed into one site so a wrong
    /// registry here cannot make the two DISAGREE — which is not the same as making it testable:
    /// a null here still leaves the suite green, because nothing headlessly drives these two call
    /// sites. What the collapse buys is the failure mode: with one site, a wrong term is wrong in
    /// both captures, so the residual is the ticket-11 defect returning (no reload). With two
    /// independent sites, one wrong term produced a phantom diff that reloaded the keyboard and
    /// the ONNX model on every apply, forever. Better residual, not a guard — read it as no more
    /// than that. The two static helpers are genuinely pinned by the direct term assertions in
    /// KeyboardCoordinatorTests.
    /// </summary>
    private KeyboardStackSnapshot CaptureSnapshot(EyeCtrlSettings settings) =>
        KeyboardStackSnapshot.Capture(settings, _modelRegistry);

    /// <summary>
    /// The reload decision itself, extracted so it is headless-testable: <see cref="ReloadIfChanged"/>
    /// returns <c>false</c> before reaching this when <c>Context</c> is null, which is every unit
    /// test — asserting on <see cref="ReloadIfChanged"/> directly would be vacuously false whatever
    /// the snapshots said. The property is "reload iff the captured inputs moved", never "reload
    /// because an apply happened": a guard weakened to always reload would rebuild the layout
    /// (resetting dwell under the user) and load a multi-hundred-MB ONNX model on every slider
    /// step.
    /// <para>
    /// Known residual, accepted rather than pinned: the WIRING that consumes this decision
    /// (<see cref="ReloadIfChanged"/>'s call into <see cref="LoadKeyboard"/>) is unreachable from
    /// a unit test through the same Context early-out, so a mutation that deletes the call
    /// survives the suite. Extracting this method made the decision testable, not the wiring; the
    /// alternative was a spelling ratchet on the <c>if</c>, which this codebase deletes on sight.
    /// </para>
    /// </summary>
    internal static bool ShouldReload(KeyboardStackSnapshot? stored, KeyboardStackSnapshot current) =>
        stored != current;

    /// <summary>
    /// PATCHES the stored snapshot's verdict term with the verdict the swapped build reported
    /// (plan review C4): never re-captures. A fresh CaptureSnapshot here would re-read the
    /// ledger AND ActiveModelOnDisk live — absorbing a download report that landed after the
    /// build checked disk (silently un-fixing the repair reload) and storing on-disk state the
    /// running stack was never built against (the ticket-11 bug, back). A null verdict
    /// (swap-to-null) keeps the stored term, i.e. the previous verdict. A verdict for a model
    /// other than the stored active model is ignored. A stored snapshot whose active model is
    /// NOT on disk (<c>ActiveModelOnDisk</c> false — e.g. the tokenizer alone is absent) is
    /// returned unchanged: the compare path's read gate (<c>ActiveModelVerificationOf</c>) reads
    /// the ledger only when <c>IsModelDownloaded</c>, so any term written here would be
    /// unreadable there and the two snapshots could never converge (I1's reload loop).
    /// </summary>
    internal static KeyboardStackSnapshot WithSwappedVerdict(
        KeyboardStackSnapshot stored,
        (string ModelId, ModelVerificationOutcome Outcome)? verification)
    {
        if (verification is not { } swap
            || !stored.ActiveModelOnDisk // I1: never write a verdict term the compare path cannot read back
            || !string.Equals(swap.ModelId, stored.MLModelSize, StringComparison.OrdinalIgnoreCase))
            return stored;
        return stored with { ActiveModelVerification = swap.Outcome };
    }

    /// <summary>
    /// The <see cref="PredictionStackFactory.StackSwapped"/> handler: re-binds the keyboard to
    /// the swapped orchestrator, then patches the stored snapshot's verdict term (patch
    /// mechanics and rationale on <see cref="PatchSnapshotWithSwappedVerdict"/>; the event is
    /// raised on the UI thread after the winning swap). Deliberately NO Context null-guard —
    /// a swap must never be silently dropped, and the subscription only exists after
    /// Initialize set it. Extracted from an inline lambda (issue #166 round-1 M2): the
    /// method-group subscription is what the wiring pin matches, because an inline lambda's
    /// body can be dead-wrapped while its call text still satisfies a substring pin.
    /// </summary>
    internal void OnStackSwapped(PredictionOrchestrator? orchestrator,
        (string ModelId, ModelVerificationOutcome Outcome)? verification)
    {
        Context!.OverlayManager.Keyboard.SetOrchestrator(orchestrator);
        PatchSnapshotWithSwappedVerdict(verification);
    }

    /// <summary>
    /// Instance seam for the swap-time snapshot PATCH the <see cref="OnStackSwapped"/> handler
    /// runs
    /// (spec N3/O2/R1 + plan review C4): the seed from LoadKeyboard stays until the first swap;
    /// each swap moves ONLY the verdict term, with the verdict the swapped build itself reported.
    /// No re-capture — a fresh one would re-read live disk/ledger state the running stack was
    /// never built against. Internal (issue #166 M2) for focused patch-arm tests; the FULL chain
    /// (live event → re-bind → patch) is covered behaviourally by
    /// KeyboardCoordinatorTests.StackSwapped_OnTheLiveFactory_RebindsTheKeyboard_AndPatchesTheSnapshot
    /// (round-2 I-1 — the chain IS headless-drivable through a real KeyboardWindow; an earlier
    /// revision of this doc wrongly claimed otherwise).
    /// </summary>
    internal void PatchSnapshotWithSwappedVerdict((string ModelId, ModelVerificationOutcome Outcome)? verification)
    {
        if (_keyboardSnapshot is not null)
            _keyboardSnapshot = WithSwappedVerdict(_keyboardSnapshot, verification);
    }

    /// <summary>
    /// Instance seam for the two NON-swap snapshot writers (<see cref="HandleKeyboardEdgeToggled"/>,
    /// <see cref="OnEmojiSkinToneChanged"/>): stores <paramref name="fresh"/> with the verdict term
    /// PRESERVED from the stored snapshot (mechanics and rationale in
    /// <see cref="WithPreservedVerdict"/>'s doc). Internal for the same reason as
    /// <see cref="PatchSnapshotWithSwappedVerdict"/> (issue #166 M2) — the handlers' remaining lines
    /// touch real windows; this assignment is the testable wiring.
    /// </summary>
    internal void CapturePreservingVerdict(KeyboardStackSnapshot fresh) =>
        _keyboardSnapshot = WithPreservedVerdict(_keyboardSnapshot, fresh);

    /// <summary>Test access to the private snapshot field the two seams above write (issue #166
    /// M2): tests seed a snapshot, drive the seam, and read back what was stored.</summary>
    internal KeyboardStackSnapshot? KeyboardSnapshotForTesting
    {
        get => _keyboardSnapshot;
        set => _keyboardSnapshot = value;
    }

    /// <summary>
    /// Guards the two NON-swap snapshot writers — the edge flip (<see cref="HandleKeyboardEdgeToggled"/>)
    /// and the skin-tone change (<see cref="OnEmojiSkinToneChanged"/>) — against the same verdict
    /// absorption <see cref="WithSwappedVerdict"/> exists to refuse (plan review C4's class, found
    /// outside the swap handler). Both writers re-capture the FULL snapshot because their own
    /// settings terms genuinely moved, but <see cref="KeyboardStackSnapshot.Capture"/> reads the
    /// verdict term LIVE from the ledger: a repair download's Verified can land there in the window
    /// between the download worker's <c>ReportVerification</c> and the <c>ApplyAndSave</c>
    /// continuation's reload, and a user action in that window would otherwise store Verified while
    /// the RUNNING stack is still the Failed one — the next apply then compares equal, skips the
    /// reload, and the LM stays dictionary-only for the session while the Models row shows the
    /// download verified. So when the active model is unchanged, the fresh capture keeps the STORED
    /// verdict: the term may only move with a swap (the running stack's own report) or through a
    /// reload. A model change takes the fresh term — different model, different ledger row. A null
    /// <paramref name="stored"/> (nothing captured yet) also takes the fresh term. A fresh
    /// capture whose active model is NOT on disk (<c>ActiveModelOnDisk</c> false) also takes the
    /// fresh term: it carries the neutral <c>NotYetVerified</c> by construction, whereas the
    /// stored term could never be read back by the compare path's gate
    /// (<c>ActiveModelVerificationOf</c> reads the ledger only when <c>IsModelDownloaded</c>) —
    /// the same unreadable-term rule <see cref="WithSwappedVerdict"/> enforces.
    /// </summary>
    internal static KeyboardStackSnapshot WithPreservedVerdict(
        KeyboardStackSnapshot? stored, KeyboardStackSnapshot fresh) =>
        stored is not null
        && fresh.ActiveModelOnDisk
        && string.Equals(stored.MLModelSize, fresh.MLModelSize, StringComparison.OrdinalIgnoreCase)
            ? fresh with { ActiveModelVerification = stored.ActiveModelVerification }
            : fresh;

    // manageSuppression: true for the toolbar toggle (holds the keyboard's fixation-suppression
    // claim while the keyboard is up); false for the text-field auto-show/hide request path, which
    // takes no claim (typing there shouldn't pause fixation for the rest of the app). Each owner
    // releases only its own claim, so this release cannot clear another owner's hold.
    private void SetKeyboardVisible(bool visible, bool manageSuppression)
    {
        if (Context is null) return;

        ApplyModalKeyboardState(visible);
        Log.Information("Keyboard {State}", visible ? "shown" : "hidden");

        if (visible)
        {
            Context.OverlayManager.Keyboard.Show();
            // F2: the explicit show placement. This is the session's one un-yield (docked
            // position plus the toolbar yield that is true now); it must run after Show() so
            // the window has its real size. The window no longer infers a show from
            // IsVisibleChanged.
            Context.OverlayManager.Keyboard.PlaceForShow();
            ApplyInitialToolbarOccupancy();
            ShowKeyboardEdgeBar();
            if (manageSuppression)
                ApplyKeyboardSuppression(visible: true);
            Context.Messenger.Send(new ShowKeyboardMessage());
        }
        else
        {
            Context.OverlayManager.Keyboard.Hide();
            if (manageSuppression)
                ApplyKeyboardSuppression(visible: false);
            HideKeyboardEdgeBar();
            Context.Messenger.Send(new HideKeyboardMessage());
        }
    }

    /// <summary>
    /// The probe's keyboard seam (build plan C13): hide a keyboard that is visible without touching
    /// its suppression claim. <c>manageSuppression:false</c> is the point — the toolbar-toggle claim
    /// is held while the keyboard is up, and the probe must leave it exactly as it was so the
    /// post-probe re-show is claim-intact (review 03 S2). Returns whether the keyboard was visible
    /// so the caller can record it; a hidden keyboard is left alone rather than sent a spurious
    /// HideKeyboardMessage. Synchronous: the caller is C7 on the UI thread.
    /// </summary>
    internal bool HideForProbe()
    {
        bool wasVisible = _modal.KeyboardVisible;
        if (wasVisible)
            SetKeyboardVisible(false, manageSuppression: false);
        return wasVisible;
    }

    /// <summary>
    /// The probe's keyboard restore (build plan C13). A <c>false</c> argument is a NO-OP (review 05
    /// F2): calling <see cref="SetKeyboardVisible"/>(false, false) here would hide a keyboard the
    /// probe never hid and leave <c>FixationSuppressor.Keyboard</c> held — no keyboard on screen and
    /// fixation still suppressed. Only a keyboard the probe actually hid is re-shown, and
    /// <c>manageSuppression:false</c> leaves the pre-probe claim untouched.
    /// </summary>
    internal void RestoreAfterProbe(bool wasVisible)
    {
        if (!wasVisible)
            return;
        SetKeyboardVisible(true, manageSuppression: false);
    }

    /// <summary>
    /// The keyboard's fixation-suppression claim, held while the keyboard is up from the toolbar
    /// toggle. Showing takes the claim and ends a fixation in flight — the detector raises no end
    /// once suppressed, and a state left at Fixation would block every FreeLook-guarded entry.
    /// Hiding releases only this claim. Internal so tests can drive it without showing the real
    /// window; <see cref="SetKeyboardVisible"/> is its only production caller.
    /// </summary>
    internal void ApplyKeyboardSuppression(bool visible)
    {
        if (Context is null) return;
        if (visible)
        {
            Context.GazeEngine.AcquireFixationSuppression(FixationSuppressor.Keyboard);
            Context.InteractionManager.OnFixationEnded();
        }
        else
        {
            Context.GazeEngine.ReleaseFixationSuppression(FixationSuppressor.Keyboard);
        }
    }

    private void HandleKeyboardEdgeToggled()
    {
        if (Context is null) return;
        var (flipped, snapshot) = ApplyKeyboardEdge(Context.Settings, Context.SettingsService, _modelRegistry);
        Context.OverlayManager.Keyboard.SetScreenEdge(flipped);
        // Verdict-preserving assignment (plan review C4, non-swap site): the fresh capture's
        // settings terms moved, but its ledger verdict term must not jump ahead of the running
        // stack — see WithPreservedVerdict.
        CapturePreservingVerdict(snapshot);
        ShowKeyboardEdgeBar();
    }

    private void ShowKeyboardEdgeBar()
    {
        if (Context is null) return;
        var bar = Context.OverlayManager.EdgeButtonBar;
        HookEdgeBarNotifications(bar);

        // Reverse-overlap guard (spec B4 #10, the OverlayMessageCoordinator hand-off dance):
        // if the bar is currently showing ANOTHER context's definitions (scroll Stop, drag
        // Cancel, the menu pair), do not clobber them. Skipping is safe in both directions:
        // showing only when the bar is free-or-ours, hiding only when ours. A skip is NOT the
        // end of the story (see the trace at B4 #10 in the keyboard spec): the keyboard stays
        // closable via the toolbar's Keyboard toggle, and the skip records _keyboardPairWanted
        // so the pair is re-claimed the moment the other context releases the bar.
        if (!ShouldShowKeyboardEdgeBar(bar.EdgeBarVm))
        {
            _keyboardPairWanted = true;
            Log.Debug("Edge bar busy with another context's buttons; keyboard pair skipped (retry when free)");
            return;
        }
        _keyboardPairWanted = false;

        var atTop = string.Equals(Context.Settings.Keyboard.ScreenEdge, "Top", StringComparison.OrdinalIgnoreCase);

        // Reaching this show with a free bar after a RING activation depends on dispatcher FIFO:
        // InteractionManager fires HideActionMenu before ActionRequested (both queued at
        // DispatcherPriority.Normal), so OverlayMessageCoordinator's close handler clears the
        // menu's pair before this show runs — one hop of margin. If that ordering ever inverts,
        // the ring action silently produces a keyboard whose Close/Flip were skipped by the
        // guard above (its Log.Debug the only trace). Same invisible guarantee as
        // OverlayMessageCoordinator.cs:119-121.
        bar.ShowButtons(
            KeyboardEdgeButtons.Definitions(KeyboardEdgeButtonDwellMs(Context.Settings.Dwell)),
            ScreenGeometry.KeyboardEdgeBarAnchor(atTop, UIConstants.ButtonRadius));
    }

    /// <summary>
    /// The keyboard edge-bar pair's dwell: the same setting the action-menu pair reads on the
    /// shared bar (<c>Dwell.ActionMenuDwellMs</c>). Close and Flip used to ride the 450 ms
    /// keyboard KEY dwell, which made the keyboard's escape and its worst-region move cheaper
    /// than every other edge-bar button. The 600 ms floor lives in
    /// <see cref="KeyboardEdgeButtons.Definitions"/> so every caller gets it.
    /// </summary>
    internal static int KeyboardEdgeButtonDwellMs(DwellSettings dwell) => dwell.ActionMenuDwellMs;

    private void HideKeyboardEdgeBar()
    {
        // The keyboard is going away — a pending retry claim dies with it.
        _keyboardPairWanted = false;

        // Guarded hand-off (spec B4 #10): the bar is shared — only clear it while the
        // keyboard's definitions still own it, mirroring OverlayMessageCoordinator's
        // ActionMenu Close guard.
        var bar = Context?.OverlayManager.EdgeButtonBar;
        if (ShouldHideKeyboardEdgeBar(bar?.EdgeBarVm))
            bar!.Hide();
    }

    /// <summary>The bar is claimable for the keyboard pair when it holds no definitions at
    /// all (the common case) or already ours (a flip rebuild).</summary>
    internal static bool ShouldShowKeyboardEdgeBar(EdgeButtonBarViewModel? barVm) =>
        barVm is null
        || barVm.Buttons.Count == 0
        || barVm.DefinitionFor(KeyboardEdgeButtons.CloseId) is not null;

    /// <summary>The keyboard's hide may clear the bar only while the keyboard pair still
    /// owns it — another context's stop/cancel buttons must survive.</summary>
    internal static bool ShouldHideKeyboardEdgeBar(EdgeButtonBarViewModel? barVm) =>
        barVm?.DefinitionFor(KeyboardEdgeButtons.CloseId) is not null;

    /// <summary>The retry decision for a keyboard pair that lost the show race (spec B4 #10
    /// reverse-overlap trace): only a keyboard that WANTS the bar, is STILL up, and sees the
    /// bar FREE again (the other context ended and its unguarded Hide emptied it) may
    /// re-claim. "Already ours" is not a retry — that is ShowKeyboardEdgeBar's own flip path.
    /// Extracted so the decision is headless-testable; the caller adds the event wiring.</summary>
    internal static bool ShouldRetryKeyboardEdgeBar(
        bool pairWanted, bool keyboardVisible, EdgeButtonBarViewModel? barVm) =>
        pairWanted && keyboardVisible && barVm is not null && barVm.Buttons.Count == 0;

    private void HookEdgeBarNotifications(IEdgeButtonBar bar)
    {
        if (_hookedEdgeBarVm is not null) return;
        var vm = bar.EdgeBarVm;
        if (vm is null) return;
        _hookedEdgeBarVm = vm;
        vm.ButtonSetChanged += OnEdgeBarButtonSetChanged;
    }

    private void OnEdgeBarButtonSetChanged()
    {
        if (Context is null) return;
        Context.Dispatcher.BeginInvoke(() =>
        {
            if (Context is null) return;
            var bar = Context.OverlayManager.EdgeButtonBar;
            if (!ShouldRetryKeyboardEdgeBar(_keyboardPairWanted, _modal.KeyboardVisible, bar.EdgeBarVm))
                return;
            Log.Debug("Edge bar freed; re-claiming it for the keyboard pair");
            ShowKeyboardEdgeBar();
        });
    }

    /// <summary>
    /// Publishes a keyboard-visibility change to the quick-actions summon-suspend seam and the
    /// shared <see cref="ModalInteractionState.KeyboardVisible"/> flag that the gaze-routing
    /// early-out in <see cref="ToolbarLifecycleCoordinator"/> reads on the gaze thread.
    /// The toolbar auto-hide is deliberately NOT suspended anymore (spec B2, ledger #9): the
    /// toolbar un-pins while the keyboard is up and edge-summons on demand; the keyboard's own
    /// escape is the edge-bar Close. Ordering matters in both directions: on show the summon
    /// suspends BEFORE the flag publishes; on hide the flag clears BEFORE the summon resumes —
    /// no gaze sample may observe <c>KeyboardVisible</c> while the quick-actions summon is live.
    /// </summary>
    internal void ApplyModalKeyboardState(bool visible)
    {
        if (visible)
        {
            _quickActions.SetSummonSuspended(true);
            _modal.KeyboardVisible = true;
        }
        else
        {
            _modal.KeyboardVisible = false;
            _quickActions.SetSummonSuspended(false);
        }
    }

    /// <summary>
    /// The persistence half of a skin-tone change: write the tone into settings, request the
    /// debounced save, and return the refreshed keyboard snapshot.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="OnEmojiSkinToneChanged"/> so the chain is reachable in a headless
    /// test (issue #58) — the handler needs an <see cref="EventCoordinationContext"/>, a 26-member
    /// record of WPF-bound objects, which is why the sibling coordinator tests never call
    /// <see cref="Initialize"/>. Same motivation as <see cref="ApplyModalKeyboardState"/>.
    ///
    /// Re-capturing the snapshot is not bookkeeping: <see cref="ReloadIfChanged"/> compares against
    /// it, so skipping the refresh would make the next settings-apply see a phantom diff and rebuild
    /// the whole keyboard (and its prediction stack) just because the user picked a skin tone.
    /// Capture must therefore happen AFTER the write.
    ///
    /// <paramref name="modelRegistry"/> is forwarded to the capture so the refreshed snapshot
    /// carries the same live <c>ActiveModelOnDisk</c> term the other capture sites compute —
    /// passing null (or any constant) here would refresh the snapshot with a term that disagrees
    /// with the next <see cref="ReloadIfChanged"/> capture and rebuild the whole keyboard on the
    /// following apply.
    /// </remarks>
    internal static KeyboardStackSnapshot ApplyEmojiSkinTone(
        EyeCtrlSettings settings, SettingsService settingsService, int tone,
        ModelRegistryService? modelRegistry)
    {
        settings.Keyboard.EmojiSkinTone = tone;
        settingsService.SaveDebounced(settings);
        return KeyboardStackSnapshot.Capture(settings, modelRegistry);
    }

    /// <summary>
    /// Pure flip: "Top" (any case) → "Bottom", anything else → "Top". The OUTPUT is always
    /// canonical — <c>KeyboardSettings.Validate</c> is a case-sensitive membership test,
    /// <c>SettingsService.Save</c> throws on failure, and <c>App.OnExit</c>'s final save is
    /// unguarded (spec B1 2a).
    /// </summary>
    internal static string FlipScreenEdge(string edge) =>
        string.Equals(edge, "Top", StringComparison.OrdinalIgnoreCase) ? "Bottom" : "Top";

    /// <summary>
    /// The persistable half of an edge flip: write the canonical flipped value, save debounced,
    /// and re-capture the reload snapshot AFTER the write (spec C5 — same discipline as
    /// <see cref="ApplyEmojiSkinTone"/>, whose remarks document the phantom-rebuild failure and
    /// the reason the registry is threaded through). The window-apply half (SetScreenEdge +
    /// edge-bar rebuild) lives in the Context-bound handler.
    /// </summary>
    internal static (string Flipped, KeyboardStackSnapshot Snapshot) ApplyKeyboardEdge(
        EyeCtrlSettings settings, SettingsService settingsService, ModelRegistryService? modelRegistry)
    {
        var flipped = FlipScreenEdge(settings.Keyboard.ScreenEdge);
        settings.Keyboard.ScreenEdge = flipped;
        settingsService.SaveDebounced(settings);
        return (flipped, KeyboardStackSnapshot.Capture(settings, modelRegistry));
    }

    /// <summary>The ring action's idempotence: already visible means the activation is a no-op.
    /// Split out so the guard is headless-testable — the handler itself touches the real
    /// KeyboardWindow (same reason as <see cref="ApplyEmojiSkinTone"/>).</summary>
    internal static bool ShouldShow(bool alreadyVisible) => !alreadyVisible;

    private void OnEmojiSkinToneChanged(int tone)
    {
        if (Context is null) return;
        // Verdict-preserving assignment (plan review C4, non-swap site): same discipline as
        // HandleKeyboardEdgeToggled — the tone term moves, the ledger verdict term must not.
        CapturePreservingVerdict(
            ApplyEmojiSkinTone(Context.Settings, Context.SettingsService, tone, _modelRegistry));
    }

    private void OnToolbarVisibilityChanged(bool toolbarVisible)
    {
        if (Context is null) return;
        Context.Dispatcher.BeginInvoke(() => ApplyToolbarOccupancy(toolbarVisible));
    }

    private void ApplyToolbarOccupancy(bool toolbarVisible)
    {
        if (Context is null) return;
        var toolbar = Context.OverlayManager.Toolbar;
        var (occupied, edge, extent) = ToolbarOccupancyFor(
            toolbarVisible, toolbar.IsVisible,
            Context.Settings.Toolbar.IsDocked, Context.Settings.Toolbar.DockedEdge,
            toolbar.ExtentDip);
        Context.OverlayManager.Keyboard.SetToolbarOccupancy(occupied, edge, extent);
    }

    /// <summary>
    /// Pure occupancy mapping for the keyboard yield (spec B3 #6/#7): the toolbar occupies the
    /// keyboard's edge only when BOTH the adapter reports a visible transition AND the window
    /// is actually on screen, and only a DOCKED toolbar has an edge at all — undocked maps to
    /// "none", which no keyboard edge matches in <see cref="KeyboardYield"/> (ledger #8).
    /// Extracted from <see cref="ApplyToolbarOccupancy"/> so the mapping is headless-testable;
    /// the caller only adds the window reads (Toolbar.IsVisible, Toolbar.ExtentDip).
    /// </summary>
    internal static (bool Occupied, string Edge, double ExtentDip) ToolbarOccupancyFor(
        bool toolbarVisible, bool toolbarWindowVisible, bool isDocked, string dockedEdge, double extentDip)
        => toolbarVisible && toolbarWindowVisible
            ? (true, isDocked ? dockedEdge : "none", extentDip)
            : (false, "none", 0);

    private void ApplyInitialToolbarOccupancy()
    {
        if (Context is null) return;
        ApplyToolbarOccupancy(Context.OverlayManager.Toolbar.IsVisible);
    }

    protected override void OnDispose()
    {
        _predictionFactory.Dispose();
        if (Context is not null)
        {
            Context.OverlayManager.Keyboard.SkinToneChanged -= OnEmojiSkinToneChanged;
            _toolbar.ToolbarVisibilityChanged -= OnToolbarVisibilityChanged;
        }
        if (_hookedEdgeBarVm is not null)
            _hookedEdgeBarVm.ButtonSetChanged -= OnEdgeBarButtonSetChanged;
    }
}
