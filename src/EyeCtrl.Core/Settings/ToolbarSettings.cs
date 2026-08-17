namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Toolbar appearance and docking settings.</summary>
public partial class ToolbarSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the toolbar is docked to a screen edge.</summary>
    [ObservableProperty] private bool _isDocked = true;
    /// <summary>Screen edge where the toolbar is docked (Top, Bottom, Left, Right).</summary>
    [ObservableProperty] private string _dockedEdge = "Top";
    /// <summary>
    /// Size of the toolbar buttons in logical pixels. Valid range: 40–200. Default 120 since
    /// 2026-08-17: at the old 80, buttons were 80 physical px on the user's display with 2-px
    /// gaps — hit circles (inscribed, size/2) tangent to their neighbours — and the user
    /// mis-dwelled constantly; one button became effectively undwellable because its neighbour
    /// kept winning the aim. 120 with 12-px margins (see ToolbarWindow.ButtonMargin) gives the
    /// same 24-px circle clearance the settings window's dwell rows use.
    /// </summary>
    [ObservableProperty] private double _size = 120;

    /// <summary>
    /// Whether <see cref="MigrateSizeOnce"/> already ran for this settings file. Absent from
    /// every pre-2026-08-17 file (loads false) so the one-time 80 -> 120 bump happens exactly
    /// once; a user who slides the size back down keeps their choice.
    /// </summary>
    [ObservableProperty] private bool _sizeMigrated;

    /// <summary>
    /// One-time migration raising the persisted old default (exactly 80) to the new 120. Any
    /// other value is a deliberate user choice and is left alone. The startup caller must use
    /// this rather than nothing: a persisted 80 would otherwise keep overriding the new default.
    /// </summary>
    public void MigrateSizeOnce()
    {
        if (SizeMigrated) return;
        if (Math.Abs(Size - 80.0) < 0.01) Size = 120;
        SizeMigrated = true;
    }

    private static readonly string[] ValidDockedEdges = { "Top", "Bottom", "Left", "Right" };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateStringEnum(DockedEdge, ValidDockedEdges, nameof(DockedEdge)),
            ValidationHelper.ValidateRange(Size, 40.0, 200.0, nameof(Size))
        );
    }
}
