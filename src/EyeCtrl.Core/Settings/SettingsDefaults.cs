namespace EyeCtrl.Core.Settings;

/// <summary>
/// Default colours for persisted settings, as hex strings.
///
/// EyeCtrl.Core cannot reference EyeCtrl.Controls (the dependency runs the other
/// way), so these cannot be EyeCtrl.Controls.Theme.Tokens directly. They are kept
/// in sync by SettingsDefaultsMatchTokensTests in EyeCtrl.App.Tests, which is the
/// only project referencing both.
/// </summary>
internal static class SettingsDefaults
{
    /// <summary>Tokens.Accent — dwell progress, cursor ring, calibration target.</summary>
    internal const string Accent = "#A5F3FC";

    /// <summary>Tokens.AccentWarning — the drag trail, deliberately distinct from Accent.</summary>
    internal const string AccentWarning = "#E8A33C";
}
