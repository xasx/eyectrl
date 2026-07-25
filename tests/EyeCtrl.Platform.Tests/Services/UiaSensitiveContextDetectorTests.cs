namespace EyeCtrl.Platform.Tests.Services;

using EyeCtrl.Platform.Services;
using Microsoft.Extensions.Logging.Abstractions;

public class UiaSensitiveContextDetectorTests
{
    // UIA focus-changed subscription needs an interactive desktop session; on non-interactive CI agents it
    // can hang, so skip gracefully there (matches the project's ControlFinderTests pattern).
    private static bool DesktopAvailable => Environment.UserInteractive;

    private const int OwnPid = 1234;
    private const int ForeignPid = 5678;

    // --- Pure focus-decision helper (headless) ---

    [Fact]
    public void EvaluateFocusedElement_OwnProcessFocus_ReturnsNull_ToRetainPreviousValue()
    {
        // Focus moving to one of EyeCtrl's own windows/overlays must NOT clear an active sensitive context:
        // the user types passwords via the on-screen keyboard, so retaining the prior value keeps suppression
        // on until focus lands on a real foreign element. (Assert on HasValue/Value rather than comparing a
        // bool? via NFluent's IsEqualTo — the project standardized on that after nullable-comparison traps.)
        var decision = UiaSensitiveContextDetector.EvaluateFocusedElement(OwnPid, OwnPid, isPassword: false);
        Check.That(decision.HasValue).IsFalse();
    }

    [Fact]
    public void EvaluateFocusedElement_ForeignPasswordField_ReturnsTrue()
    {
        var decision = UiaSensitiveContextDetector.EvaluateFocusedElement(ForeignPid, OwnPid, isPassword: true);
        Check.That(decision.HasValue).IsTrue();
        Check.That(decision!.Value).IsTrue();
    }

    [Fact]
    public void EvaluateFocusedElement_ForeignNonPasswordField_ReturnsFalse()
    {
        var decision = UiaSensitiveContextDetector.EvaluateFocusedElement(ForeignPid, OwnPid, isPassword: false);
        Check.That(decision.HasValue).IsTrue();
        Check.That(decision!.Value).IsFalse();
    }

    // --- Constructor is side-effect-free (headless: no desktop needed) ---

    [Fact]
    public void NewDetector_IsNotSensitive_AndCtorTouchesNoUia()
    {
        // The ctor only assigns fields (UIA is touched in Start(), mirroring the Tobii provider), so
        // constructing it is safe without a desktop and the default is the safe not-sensitive value.
        var detector = new UiaSensitiveContextDetector(NullLogger<UiaSensitiveContextDetector>.Instance);
        Check.That(detector.IsSensitiveActive).IsFalse();
        detector.Dispose(); // Dispose before Start() is a no-op and must not throw
    }

    // --- UIA subscription smoke test (desktop-gated; build/integration tier) ---

    [Fact]
    public void StartStopDispose_OnInteractiveDesktop_DoNotThrow()
    {
        if (!DesktopAvailable) return;

        // Smoke test: the real UIA add/remove path runs without throwing and Start/Stop are idempotent.
        // We deliberately do NOT assert IsSensitiveActive after Dispose: on a live desktop a stray foreign
        // password field could gain focus during this window and flip it, so the reset-to-false behaviour
        // is a design property that cannot be asserted race-free headlessly. No-throw/idempotency is the value.
        var detector = new UiaSensitiveContextDetector(NullLogger<UiaSensitiveContextDetector>.Instance);
        detector.Start();
        detector.Start();   // idempotent — must not double-subscribe or throw
        detector.Stop();
        detector.Stop();    // idempotent
        detector.Dispose();
    }
}
