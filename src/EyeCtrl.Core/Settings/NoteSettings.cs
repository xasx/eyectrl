namespace EyeCtrl.Core.Settings;

using CommunityToolkit.Mvvm.ComponentModel;

/// <summary>
/// Pinned-note appearance settings (P4 D1). Currently one value: the note body text size in
/// DIP. The provisional default is 28 - the reading-test arithmetic in P4 ("Why this shape")
/// puts comfortable reading at 26-33 DIP for a caretaker at 0.8 m from an 18-inch panel at
/// 200 percent - and the owner's reading test (P4 D5) decides the final constant.
/// </summary>
public partial class NoteSettings : ObservableObject, IValidatable
{
    /// <summary>The guard's and the JSON clamp's shared bounds (P4 D1): 18-48 DIP.</summary>
    public const int MinTextSizeDip = 18;

    /// <summary>The guard ceiling: 48 DIP — the fit harness runs at 48 (P4 Risks).</summary>
    public const int MaxTextSizeDip = 48;

    /// <summary>Provisional default until the owner's reading test (P4 D5) fixes the constant.</summary>
    public const int DefaultTextSizeDip = 28;

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
