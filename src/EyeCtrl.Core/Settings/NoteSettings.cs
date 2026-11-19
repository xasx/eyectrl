namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Pinned-note appearance settings (P4 D1). Currently one value: the note body text size in
/// DIP. The default is 36, the owner's pick from the 19.11 comparison of 28, 36 and 40 DIP at
/// SemiBold on the owner's own display, after sitting 9 (18.11) failed the 28 DIP reading row ("Note
/// text hard"). It replaces the provisional 28 that P4's reading-test arithmetic ("Why this
/// shape") had suggested: 26-33 DIP for a caretaker at 0.8 m from an 18-inch panel at
/// 200 percent.
/// </summary>
public partial class NoteSettings : ObservableObject, IValidatable
{
    /// <summary>The guard's and the JSON clamp's shared bounds (P4 D1): 18-48 DIP.</summary>
    public const int MinTextSizeDip = 18;

    /// <summary>The guard ceiling: 48 DIP — the fit harness runs at 48 (P4 Risks).</summary>
    public const int MaxTextSizeDip = 48;

    /// <summary>The default note body text size in DIP: 36. The owner's pick from the 19.11
    /// comparison (28/36/40 DIP at SemiBold); replaces the provisional 28.</summary>
    public const int DefaultTextSizeDip = 36;

    /// <summary>The note body text size in DIP. Changing it re-lays out every live note (P3 D3's T8).</summary>
    [ObservableProperty]
    private int _textSizeDip = DefaultTextSizeDip;

    /// <summary>
    /// The throwing guard (P4 D1, the AdaptationSettings GuardRange pattern): a Changing hook
    /// cannot alter the incoming value and the UI never offers an illegal one, so out-of-range
    /// means a bug or a hand-edited write - loud, at the property.
    /// </summary>
    partial void OnTextSizeDipChanging(int value)
    {
        if (value < MinTextSizeDip || value > MaxTextSizeDip)
            throw new ArgumentOutOfRangeException(nameof(value), value,
                $"TextSizeDip must be between {MinTextSizeDip} and {MaxTextSizeDip}.");
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Validate() => ValidationHelper.CollectErrors(
        ValidationHelper.ValidateRange(TextSizeDip, MinTextSizeDip, MaxTextSizeDip, nameof(TextSizeDip)));
}
