namespace EyeCtrl.App.Tests.Services;

using System;
using Xunit;
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
}
