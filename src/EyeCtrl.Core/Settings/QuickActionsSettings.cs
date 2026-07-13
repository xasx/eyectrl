namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>Quick Actions toolbar appearance and summoning settings.</summary>
public sealed partial class QuickActionsSettings : ObservableObject, IValidatable
{
    /// <summary>Whether the Quick Actions toolbar is enabled.</summary>
    [ObservableProperty] private bool _enabled = true;
    /// <summary>Screen edge that summons the toolbar when gaze dwells on it (Top, Bottom, Left, Right).</summary>
    [ObservableProperty] private string _summonEdge = "Left";
    /// <summary>Gaze dwell time in milliseconds required to summon the toolbar. Valid range: 200–2000.</summary>
    [ObservableProperty] private int _summonThresholdMs = 400;
    /// <summary>Inactivity timeout in milliseconds after which the toolbar auto-dismisses. Valid range: 1000–15000.</summary>
    [ObservableProperty] private int _dismissTimeoutMs = 5000;
    /// <summary>Number of columns in the action grid. Valid range: 2–5.</summary>
    [ObservableProperty] private int _gridColumns = 3;

    private static readonly string[] ValidEdges = { "Top", "Bottom", "Left", "Right" };

    /// <inheritdoc />
    public IReadOnlyList<string> Validate()
    {
        return ValidationHelper.CollectErrors(
            ValidationHelper.ValidateRange(SummonThresholdMs, 200, 2000, nameof(SummonThresholdMs)),
            ValidationHelper.ValidateRange(DismissTimeoutMs, 1000, 15000, nameof(DismissTimeoutMs)),
            ValidationHelper.ValidateRange(GridColumns, 2, 5, nameof(GridColumns)),
            ValidationHelper.ValidateStringEnum(SummonEdge, ValidEdges, nameof(SummonEdge))
        );
    }
}
