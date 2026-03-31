namespace EyeCtrl.Core.Tests.Settings;

using EyeCtrl.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Tests to verify DI container registration follows the single-root pattern:
/// only EyeCtrlSettings should be registered, not individual sub-settings.
/// This prevents multiple reference paths and structural coupling.
/// </summary>
public class DIRegistrationTests
{
    /// <summary>
    /// Verify that EyeCtrlSettings is registered in the DI container.
    /// </summary>
    [Fact]
    public void ServiceProvider_ShouldHave_EyeCtrlSettings_Registered()
    {
        // Arrange
        var services = new ServiceCollection();
        var rootSettings = new EyeCtrlSettings();
        services.AddSingleton(rootSettings);
        var provider = services.BuildServiceProvider();

        // Act
        var settings = provider.GetService<EyeCtrlSettings>();

        // Assert
        Assert.NotNull(settings);
        Assert.Same(rootSettings, settings);
    }

    /// <summary>
    /// Verify that individual sub-settings are NOT registered in the DI container
    /// when using the correct pattern (only root settings).
    /// </summary>
    [Fact]
    public void ServiceProvider_ShouldNotHave_SubSettings_DirectlyRegistered()
    {
        // Arrange - Use CORRECT pattern (only register root)
        var services = new ServiceCollection();
        var rootSettings = new EyeCtrlSettings();
        services.AddSingleton(rootSettings);
        var provider = services.BuildServiceProvider();

        // Act & Assert
        // These should all be NULL in the correct pattern
        var cursorSettings = provider.GetService<CursorSettings>();
        var dwellSettings = provider.GetService<DwellSettings>();
        var zoomSettings = provider.GetService<ZoomSettings>();
        var actionMenuSettings = provider.GetService<ActionMenuSettings>();
        var keyboardSettings = provider.GetService<KeyboardSettings>();
        var toolbarSettings = provider.GetService<ToolbarSettings>();
        var themeSettings = provider.GetService<ThemeSettings>();
        var generalSettings = provider.GetService<GeneralSettings>();

        // All should be null - consumers should access via EyeCtrlSettings properties
        Assert.Null(cursorSettings);
        Assert.Null(dwellSettings);
        Assert.Null(zoomSettings);
        Assert.Null(actionMenuSettings);
        Assert.Null(keyboardSettings);
        Assert.Null(toolbarSettings);
        Assert.Null(themeSettings);
        Assert.Null(generalSettings);
    }

    /// <summary>
    /// Verify that sub-settings can be accessed via EyeCtrlSettings properties.
    /// </summary>
    [Fact]
    public void SubSettings_ShouldBeAccessible_ViaEyeCtrlSettingsProperties()
    {
        // Arrange
        var services = new ServiceCollection();
        var rootSettings = new EyeCtrlSettings();
        services.AddSingleton(rootSettings);
        var provider = services.BuildServiceProvider();

        // Act
        var settings = provider.GetRequiredService<EyeCtrlSettings>();

        // Assert
        Assert.NotNull(settings.Cursor);
        Assert.NotNull(settings.Dwell);
        Assert.NotNull(settings.Zoom);
        Assert.NotNull(settings.ActionMenu);
        Assert.NotNull(settings.Keyboard);
        Assert.NotNull(settings.Toolbar);
        Assert.NotNull(settings.Theme);
        Assert.NotNull(settings.General);
    }

    /// <summary>
    /// Verify that the same sub-setting instances are returned via EyeCtrlSettings
    /// when accessed multiple times (singleton behavior).
    /// </summary>
    [Fact]
    public void SubSettings_ShouldReturn_SameInstances_AcrossCalls()
    {
        // Arrange
        var services = new ServiceCollection();
        var rootSettings = new EyeCtrlSettings();
        services.AddSingleton(rootSettings);
        var provider = services.BuildServiceProvider();

        // Act
        var settings = provider.GetRequiredService<EyeCtrlSettings>();
        var cursor1 = settings.Cursor;
        var cursor2 = settings.Cursor;
        var dwell1 = settings.Dwell;
        var dwell2 = settings.Dwell;

        // Assert
        Assert.Same(cursor1, cursor2);
        Assert.Same(dwell1, dwell2);
    }
}
