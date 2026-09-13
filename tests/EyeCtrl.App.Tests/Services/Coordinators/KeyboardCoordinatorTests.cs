namespace EyeCtrl.App.Tests.Services.Coordinators;

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.App.Services.Coordinators;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using Xunit;

public class KeyboardCoordinatorTests
{
    // The ctor needs a ModalInteractionState, a QuickActionsCoordinator, and a
    // ToolbarLifecycleCoordinator. None of these are touched by the lifecycle contract tests below
    // (they never call Initialize), so plain constructed instances suffice — the same shape the
    // sibling coordinator tests use.
    private static KeyboardCoordinator NewCoordinator() =>
        new(new ModalInteractionState(),
            new QuickActionsCoordinator(),
            new ToolbarLifecycleCoordinator(new ModalInteractionState()));

    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        var c = NewCoordinator();
        Check.ThatCode(() => c.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Mirrors the EventCoordinator contract test: a constructed-but-uninitialized coordinator
        // must still dispose cleanly (no NullRef on _context). The PredictionStackFactory is
        // field-initialized, so its Dispose must run even when Initialize never did.
        NewCoordinator().Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        var c = NewCoordinator();
        c.Dispose();
        c.Dispose();
    }

    [Fact]
    public void WordPredictor_ReturnsNull_BeforeLoadKeyboard()
    {
        // The 8-member public surface (re-exposed on EventCoordinator) includes WordPredictor and
        // Orchestrator; both must read null until LoadKeyboard builds a prediction stack.
        var c = NewCoordinator();
        Check.That(c.WordPredictor).IsNull();
        Check.That(c.Orchestrator).IsNull();
    }

    [Fact]
    public void Ctor_RejectsNullCollaborators()
    {
        var modal = new ModalInteractionState();
        var quickActions = new QuickActionsCoordinator();
        var toolbar = new ToolbarLifecycleCoordinator(modal);

        Check.ThatCode(() => new KeyboardCoordinator(null!, quickActions, toolbar)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new KeyboardCoordinator(modal, null!, toolbar)).Throws<ArgumentNullException>();
        Check.ThatCode(() => new KeyboardCoordinator(modal, quickActions, null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void ApplyModalKeyboardState_FansOutToQuickActionsSummon_AndPublishesTheSharedFlag()
    {
        // Spec B2 (ledger #9): showing the keyboard suspends ONLY the quick-actions hot-edge
        // summon and publishes the shared ModalInteractionState flag the gaze-thread early-out
        // reads. The toolbar auto-hide suspension is deliberately gone — the toolbar un-pins
        // while the keyboard is up, and the keyboard's own escape is the edge-bar Close.
        var modal = new ModalInteractionState();

        var summon = new QuickActionsSummonController("left", 1920, 1080, 400, 5000);
        var quickActions = new QuickActionsCoordinator();
        quickActions.InjectSummonForTesting(summon);

        var autoHide = new ToolbarAutoHideController(
            enabled: true, edge: "bottom", screenW: 1920, screenH: 1080,
            toolbarRect: new PhysicalRect(0, 1040, 1920, 40),
            hideDelayMs: 5000, summonDelayMs: 400, offScreenMarginPx: 50);
        var toolbar = new ToolbarLifecycleCoordinator(modal);
        toolbar.InjectAutoHideForTesting(autoHide);

        var keyboard = new KeyboardCoordinator(modal, quickActions, toolbar);

        keyboard.ApplyModalKeyboardState(true);
        Check.That(modal.KeyboardVisible).IsTrue();
        Check.That(quickActions.IsSummonSuspended).IsTrue();
        Check.That(autoHide.IsSuspended).IsFalse();

        keyboard.ApplyModalKeyboardState(false);
        Check.That(modal.KeyboardVisible).IsFalse();
        Check.That(quickActions.IsSummonSuspended).IsFalse();
        Check.That(autoHide.IsSuspended).IsFalse();
    }

    // ---- Emoji skin-tone persistence (issue #58) -------------------------------------------
    // EmojiGridManagerTests already covers the event-raising half (SkinToneChanged fires with a
    // clamped value). What was untested is what happens after: that the tone reaches settings, is
    // actually written to disk, and refreshes the reload snapshot.

    [Fact]
    public void ApplyEmojiSkinTone_WritesToneIntoSettings()
    {
        var settings = new EyeCtrlSettings();
        using var temp = new TempSettingsFile();

        KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 5);

        Check.That(settings.Keyboard.EmojiSkinTone).IsEqualTo(5);
    }

    [Fact]
    public void ApplyEmojiSkinTone_ReturnsSnapshotCapturedAfterTheWrite()
    {
        // The ordering guard. ReloadIfChanged compares the stored snapshot against a fresh capture,
        // so a snapshot taken BEFORE the write would differ from the live settings and trigger a
        // full keyboard + prediction-stack rebuild on the next settings-apply.
        var settings = new EyeCtrlSettings();
        using var temp = new TempSettingsFile();

        var snapshot = KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 3);

        Check.That(snapshot.EmojiSkinTone).IsEqualTo(3);
        Check.That(snapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings));
    }

    [Fact]
    public async Task ApplyEmojiSkinTone_PersistsToneToDisk()
    {
        // The half issue #58 called out as untested: the tone must survive a restart. Drives the
        // real SettingsService against a temp file rather than asserting a call was made.
        var settings = new EyeCtrlSettings();
        using var temp = new TempSettingsFile();

        KeyboardCoordinator.ApplyEmojiSkinTone(settings, temp.Service, 4);

        var loaded = await temp.WaitForToneAsync(4, TestContext.Current.CancellationToken);
        Check.That(loaded.Keyboard.EmojiSkinTone).IsEqualTo(4);
    }

    /// <summary>
    /// A real <see cref="SettingsService"/> over a throwaway file. SaveDebounced is a 500ms
    /// debounce onto the thread pool, so the assertion polls for the value the way
    /// SettingsServiceDebounceTests does — a fixed delay flakes on a loaded runner.
    /// </summary>
    private sealed class TempSettingsFile : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), $"eyectrl_skintone_{Guid.NewGuid()}.json");

        public SettingsService Service { get; }

        public TempSettingsFile() => Service = new SettingsService(_path);

        public async Task<EyeCtrlSettings> WaitForToneAsync(int expected, CancellationToken ct)
        {
            for (var i = 0; i < 100; i++)
            {
                // Require the file to exist first: Load() returns defaults when it is missing, and
                // the default tone (0) could otherwise pass as a false positive.
                if (File.Exists(_path))
                {
                    var loaded = Service.Load();
                    if (loaded.Keyboard.EmojiSkinTone == expected) return loaded;
                }
                await Task.Delay(50, ct);
            }
            return Service.Load();
        }

        public void Dispose()
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(_path)) File.Delete(_path);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }

    [Fact]
    public void ShouldShow_IsFalse_WhenAlreadyVisible_AndTrueOtherwise()
    {
        // The ring's Keyboard action is an idempotent SHOW (spec ledger #1): the guard is the
        // whole seam — the handler itself touches WPF windows and is not headless-reachable
        // (same reason ApplyEmojiSkinTone was extracted).
        Check.That(KeyboardCoordinator.ShouldShow(alreadyVisible: true)).IsFalse();
        Check.That(KeyboardCoordinator.ShouldShow(alreadyVisible: false)).IsTrue();
    }

    // ---- Screen-edge flip (Keyboard ring action, Task 5) ------------------------------------
    // The flip core behind HandleKeyboardEdgeToggled: a pure case-normalizing flip plus a
    // persistable half that mutates settings, saves debounced, and refreshes the reload snapshot.

    [Fact]
    public void FlipScreenEdge_FlipsBothWays_AndNormalizesToCanonicalCase()
    {
        // Comparison is case-insensitive but the OUTPUT is canonical: validation is a
        // case-sensitive membership test and the unguarded exit-time save turns a stray
        // casing into an app crash (spec B1 2a).
        Check.That(KeyboardCoordinator.FlipScreenEdge("Top")).IsEqualTo("Bottom");
        Check.That(KeyboardCoordinator.FlipScreenEdge("Bottom")).IsEqualTo("Top");
        Check.That(KeyboardCoordinator.FlipScreenEdge("top")).IsEqualTo("Bottom");
        Check.That(KeyboardCoordinator.FlipScreenEdge("garbage")).IsEqualTo("Top");
    }

    [Fact]
    public void ApplyKeyboardEdge_MutatesSettings_PersistsAndReturnsFreshSnapshot()
    {
        var settings = new EyeCtrlSettings { Keyboard = { ScreenEdge = "Bottom" } };
        using var temp = new TempSettingsFile();

        var (flipped, snapshot) = KeyboardCoordinator.ApplyKeyboardEdge(settings, temp.Service);

        Check.That(flipped).IsEqualTo("Top");
        Check.That(settings.Keyboard.ScreenEdge).IsEqualTo("Top");
        // The phantom-rebuild guard (spec C5): the snapshot must reflect the write, or the
        // next settings-apply rebuilds the whole keyboard + prediction stack for nothing.
        Check.That(snapshot).IsEqualTo(KeyboardStackSnapshot.Capture(settings));
    }
}
