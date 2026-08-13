namespace EyeCtrl.App.Services.Coordinators;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels.Messages;
using Serilog;

/// <summary>
/// Owns the keyboard lifecycle surface of <see cref="EventCoordinator"/>: keyboard show/hide
/// (<see cref="SetKeyboardVisible"/>, the toggle/show/hide request messages), layout loading
/// (<see cref="LoadKeyboard"/>), the emoji skin-tone handler, and the snapshot-driven reload seam
/// (<see cref="ReloadIfChanged"/>, consumed by SettingsUiCoordinator). Extracted from
/// <c>EventCoordinator</c> as part of the sub-coordinator decomposition (cluster 3).
/// </summary>
internal sealed class KeyboardCoordinator : IDisposable
{
    private EventCoordinationContext? _context;
    private readonly ModalInteractionState _modal;
    private readonly QuickActionsCoordinator _quickActions;
    private readonly ToolbarLifecycleCoordinator _toolbar;
    private readonly PredictionStackFactory _predictionFactory = new();
    private KeyboardStackSnapshot? _keyboardSnapshot;
    // Owned HERE — moved from EventCoordinator.Initialize. Used by LoadKeyboard's
    // recent-emoji path and as the model-dir root for PredictionStackFactory.Rebuild.
    private string? _appDataDir;
    // SetModelServices hands only the registry to PredictionStackFactory.Rebuild; the download
    // service is dead and dropped (see EC.SetModelServices, kept 3-arg for App compatibility).
    private ModelRegistryService? _modelRegistry;
    private bool _disposed;

    public WordPredictor? WordPredictor => _predictionFactory.Current?.WordPredictor;
    public PredictionOrchestrator? Orchestrator => _predictionFactory.Current;

    public KeyboardCoordinator(ModalInteractionState modal,
        QuickActionsCoordinator quickActions, ToolbarLifecycleCoordinator toolbar)
    {
        // All three are dereferenced unconditionally by ApplyModalKeyboardState, so reject nulls at
        // the ctor rather than at first keyboard toggle.
        _modal = modal ?? throw new ArgumentNullException(nameof(modal));
        _quickActions = quickActions ?? throw new ArgumentNullException(nameof(quickActions));
        _toolbar = toolbar ?? throw new ArgumentNullException(nameof(toolbar));
    }

    public void Initialize(EventCoordinationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context)); // MANDATORY — see "How to read"
        // Taken from the context, NOT derived from %APPDATA%: the composition root resolves the
        // profile once, and a scoped instance's directory is not something this class can compute.
        // Deriving it here would quietly reach into the live profile on every scoped run.
        _appDataDir = context.AppDataDir;

        var messenger = context.Messenger;

        // Keyboard toggle
        messenger.Register<KeyboardCoordinator, KeyboardToggledMessage>(this, (_, _) =>
            context.Dispatcher.BeginInvoke(() => SetKeyboardVisible(!_modal.KeyboardVisible, manageSuppression: true)));

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

        context.OverlayManager.Keyboard.SkinToneChanged += OnEmojiSkinToneChanged;
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
            var layout = KeyboardLayoutLoader.Load(layoutPath);
            keyboard.LoadLayout(layout,
                settings.Keyboard.KeySizeMultiplier,
                settings.Dwell.KeyboardKeyDwellMs,
                settings.Dwell.KeyboardPredictionDwellMs,
                settings.Dwell.DwellCooldownMs);

            var emojiPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "Emojis", "emojis.json");
            var recentPath = Path.Combine(_appDataDir!, "recent_emojis.json");
            keyboard.SetEmojiData(emojiPath, recentPath);
            keyboard.SetSkinTone(settings.Keyboard.EmojiSkinTone);
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

        _keyboardSnapshot = KeyboardStackSnapshot.Capture(settings);
    }

    /// <summary>Seam consumed by SettingsUiCoordinator (T7): reload the keyboard iff the
    /// layout-affecting settings changed, so T7 doesn't read <c>_keyboardSnapshot</c>.
    /// Returns <c>true</c> if reloaded.</summary>
    internal bool ReloadIfChanged(EyeCtrlSettings settings, IInputSimulator input, SettingsService ss)
    {
        var snap = KeyboardStackSnapshot.Capture(settings);
        if (_keyboardSnapshot == snap) return false;
        if (_context is null) return false; // defensive — the seam is only reachable post-Initialize
        LoadKeyboard(_context.OverlayManager.Keyboard, settings, input, ss);
        return true;
    }

    // manageSuppression: true for the toolbar toggle (deliberately pauses fixation while the
    // keyboard is up); false for the text-field auto-show/hide request path, which must NOT touch
    // SuppressFixation (it may be owned elsewhere, e.g. note editing, and typing shouldn't pause
    // fixation for the rest of the app).
    private void SetKeyboardVisible(bool visible, bool manageSuppression)
    {
        if (_context == null) return;

        ApplyModalKeyboardState(visible);
        Log.Information("Keyboard {State}", visible ? "shown" : "hidden");

        if (visible)
        {
            _context.OverlayManager.Keyboard.Show();
            if (manageSuppression)
            {
                _context.GazeEngine.SuppressFixation = true;
                _context.InteractionManager.OnFixationEnded();
            }
            _context.Messenger.Send(new ShowKeyboardMessage());
        }
        else
        {
            _context.OverlayManager.Keyboard.Hide();
            if (manageSuppression)
                _context.GazeEngine.SuppressFixation = false;
            _context.Messenger.Send(new HideKeyboardMessage());
        }
    }

    /// <summary>
    /// Publishes a keyboard-visibility change to the two cross-cluster consumers: the toolbar
    /// auto-hide and quick-actions summon suspend seams, plus the shared
    /// <see cref="ModalInteractionState.KeyboardVisible"/> flag that the gaze-routing early-out in
    /// <see cref="ToolbarLifecycleCoordinator"/> reads on the gaze thread.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="SetKeyboardVisible"/> so the fan-out is reachable in a headless test:
    /// the remainder of SetKeyboardVisible touches the real <c>KeyboardWindow</c>. Order matters —
    /// suspend the controllers BEFORE publishing the flag, so no gaze sample can observe
    /// KeyboardVisible while auto-hide is still live.
    /// </remarks>
    internal void ApplyModalKeyboardState(bool visible)
    {
        _toolbar.SetAutoHideSuspended(visible);
        _quickActions.SetSummonSuspended(visible);
        _modal.KeyboardVisible = visible;
    }

    private void OnEmojiSkinToneChanged(int tone)
    {
        if (_context == null) return;
        _context.Settings.Keyboard.EmojiSkinTone = tone;
        _context.SettingsService.SaveDebounced(_context.Settings);
        _keyboardSnapshot = KeyboardStackSnapshot.Capture(_context.Settings);
    }

    public void Dispose()
    {
        if (_disposed) return;

        // The prediction factory is field-initialized (never null) and its Dispose is idempotent;
        // dispose it regardless of whether Initialize ran — a constructed-but-uninitialized
        // coordinator (_context == null) must still release it rather than skip disposal.
        _predictionFactory.Dispose();

        if (_context is not null)
        {
            _context.OverlayManager.Keyboard.SkinToneChanged -= OnEmojiSkinToneChanged;
            _context.Messenger.UnregisterAll(this);
        }

        _disposed = true;
    }
}
