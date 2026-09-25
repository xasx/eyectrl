namespace EyeCtrl.Core.Keyboard;

/// <summary>A named sub-layout containing rows of keys (e.g., "letters" or "symbols").</summary>
public class SubLayout
{
    /// <summary>Display label for this sub-layout (shown on the sub-layout toggle button).</summary>
    public string Label { get; set; } = "";
    /// <summary>Grid of key rows, each row being a list of <see cref="KeyDefinition"/> instances.</summary>
    public List<List<KeyDefinition>> Rows { get; set; } = new();
    /// <summary>True when the keyboard returns to the layout's default sub-layout after any
    /// non-special key activation or prediction selection (spec D5). Used by the accents
    /// sub-layouts: an accent is always part of ongoing typing, so the user should land back
    /// on letters without a manual switch. Defaults to false — an unset flag must never
    /// change existing sub-layout behavior.</summary>
    public bool AutoReturn { get; set; }
}
