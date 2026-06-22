namespace EyeCtrl.Core.Keyboard;

/// <summary>A named sub-layout containing rows of keys (e.g., "letters" or "symbols").</summary>
public class SubLayout
{
    /// <summary>Display label for this sub-layout (shown on the sub-layout toggle button).</summary>
    public string Label { get; set; } = "";
    /// <summary>Grid of key rows, each row being a list of <see cref="KeyDefinition"/> instances.</summary>
    public List<List<KeyDefinition>> Rows { get; set; } = new();
}
