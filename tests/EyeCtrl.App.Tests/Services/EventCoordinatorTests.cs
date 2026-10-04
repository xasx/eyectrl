namespace EyeCtrl.App.Tests.Services;

using EyeCtrl.App.Services;

/// <summary>
/// Tests for EventCoordinator.
/// Note: EventCoordinator is tightly coupled to WPF windows and ViewModels, making comprehensive
/// unit testing challenging without extensive mocking infrastructure. These tests focus on
/// basic contract verification. Full behavioral testing is performed through integration tests.
/// </summary>
public class EventCoordinatorTests
{
    [Fact]
    public void Initialize_ThrowsArgumentNullException_WhenContextIsNull()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert
        Check.ThatCode(() => coordinator.Initialize(null!)).Throws<ArgumentNullException>();
    }

    [Fact]
    public void WordPredictor_ReturnsNull_BeforeInitialization()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act
        var predictor = coordinator.WordPredictor;

        // Assert
        Check.That(predictor).IsNull();
    }

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes_WithoutThrowing()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert - should not throw on multiple disposals
        coordinator.Dispose();
        coordinator.Dispose();
        coordinator.Dispose();
    }

    [Fact]
    public void Dispose_CanBeCalledBeforeInitialize_WithoutThrowing()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert - should not throw when disposed without initialization
        coordinator.Dispose();
    }

    [Fact]
    public void LoadKeyboard_ThrowsArgumentNullException_WhenKeyboardIsNull()
    {
        // Arrange
        var coordinator = new EventCoordinator();

        // Act & Assert
        Check.ThatCode(() =>
            coordinator.LoadKeyboard(null!, null!, null!, null!)).Throws<ArgumentNullException>();
    }

    // ── Startup-order guards on the three forwards that carry state into the sub-coordinators ──
    // The sub-coordinators only exist post-Initialize, so a null-conditional forward would DROP the
    // hand-off silently and resurface much later (ShowSettingsWindow's "Model services not
    // initialized", on a user-visible path). These pin the fail-loud contract instead. Args are null!
    // because the state guard runs before any argument is touched — that ordering is the point.

    [Fact]
    public void SetModelServices_BeforeInitialize_ThrowsInvalidOperationException()
    {
        var coordinator = new EventCoordinator();

        Check.ThatCode(() => coordinator.SetModelServices(null!, null!, null!))
            .Throws<InvalidOperationException>();
    }

    [Fact]
    public void SetRestartCoordinator_BeforeInitialize_ThrowsInvalidOperationException()
    {
        var coordinator = new EventCoordinator();

        Check.ThatCode(() => coordinator.SetRestartCoordinator(null!))
            .Throws<InvalidOperationException>();
    }

    // NOTE on LoadKeyboard: its state guard calls the same RequireInitialized helper the two tests
    // above pin, but reaching it needs all four args non-null — including a real WPF KeyboardWindow,
    // which would make this an STA/environmental test. The guard LOGIC is covered here; the
    // LoadKeyboard call site is review-verified. (Without that guard the pre-decomposition
    // "must be initialized" diagnostic degrades to a bare NullReferenceException.)

    [Fact]
    public void SetModelServices_AfterDispose_ThrowsObjectDisposedException()
    {
        // Dispose nulls the sub-coordinator fields but leaves _context set, so a context-only guard
        // would pass and then NullReference on the forward.
        var coordinator = new EventCoordinator();
        coordinator.Dispose();

        Check.ThatCode(() => coordinator.SetModelServices(null!, null!, null!))
            .Throws<ObjectDisposedException>();
    }

    [Fact]
    public void Initialize_WiresTheLostGazeExit()
    {
        // Source pin over comment-stripped, whitespace-collapsed code: Initialize needs the whole
        // window/VM graph (see the class note), so this is the only guard that the lost-gaze exit
        // (ticket 02) is constructed, initialized and disposed in production. Without it a lost-gaze
        // drag keeps the OS button pressed and a lost-gaze scroll keeps scrolling.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(System.IO.File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "EventCoordinator.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage("EventCoordinator.Initialize no longer creates and initializes the lost-gaze exit.")
            .That(source.Contains("_lostGazeExit=newLostGazeExitCoordinator();_lostGazeExit.Initialize(context);")).IsTrue();
        Check.WithCustomMessage("EventCoordinator.Dispose no longer disposes the lost-gaze exit.")
            .That(source.Contains("_lostGazeExit?.Dispose();")).IsTrue();
    }
}
