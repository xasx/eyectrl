namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>
/// F21's runtime fit cap: at the 2.0x setting the 14-unit QWERTZ letters row is wider than a
/// 1920-DIP work area, so the effective key size is reduced to the largest size at which the
/// widest row of ANY page fits. The size must be the same on every page (a per-page size would
/// move the keys under the resting gaze at a page switch, D13) and the code must never throw:
/// Math.Clamp throws when its range inverts, and this app turns a dispatcher exception into a
/// process kill.
/// </summary>
public class KeyboardFitCapTests
{
    private static KeyDefinition Key(double width = 1.0) => new() { Label = "k", Output = "k", Width = width };

    private static SubLayout Page(int keyCount)
    {
        var row = new List<KeyDefinition>();
        for (var i = 0; i < keyCount; i++) row.Add(Key());
        return new SubLayout { Label = "page", Rows = [row] };
    }

    private static KeyboardLayout Layout(params (string Id, int Keys)[] pages)
    {
        var layout = new KeyboardLayout { Name = "synthetic", DefaultSubLayout = pages[0].Id };
        foreach (var (id, keys) in pages) layout.SubLayouts[id] = Page(keys);
        return layout;
    }

    [Fact]
    public void RowWidthDip_IsTheWindowsOwnRowArithmetic()
    {
        // KeyboardWindow.RebuildKeys: keySize*w + (w>1 ? (w-1)*4 : 0) + 4 per key. At the owner's
        // 1.75x (122.5 DIP) the QWERTZ letters row 0 is 2.6 + 10x1.0 + 1.4 units wide:
        // 328.9 + 10*126.5 + 177.1 = 1771 DIP.
        KeyDefinition[] row = [Key(2.6), .. Enumerable.Range(0, 10).Select(_ => Key()), Key(1.4)];

        var width = KeyboardFitCap.RowWidthDip(row, 70 * 1.75);

        Check.That(width).IsCloseTo(1771.0, 1e-9);
    }

    [Fact]
    public void MaxKeySize_IsTheLargestSizeAtWhichTheWidestRowFits()
    {
        var layout = Layout(("letters", 10));

        var max = KeyboardFitCap.MaxKeySizeDip(layout, availableWidthDip: 1000);

        // 10 plain keys: 10 * (size + 4) = 1000 -> size 96.
        Check.That(max).IsCloseTo(96.0, 1e-9);
        Check.That(KeyboardFitCap.PageWidthDip(layout.SubLayouts["letters"], max)).IsCloseTo(1000.0, 1e-9);
        Check.WithCustomMessage("a key one DIP larger must overflow the fitted width")
            .That(KeyboardFitCap.PageWidthDip(layout.SubLayouts["letters"], max + 1.0))
            .IsStrictlyGreaterThan(1000.0);
    }

    [Fact]
    public void EffectiveKeySize_KeepsTheRequestedSize_WhenItAlreadyFits_AndCapsItOtherwise()
    {
        var layout = Layout(("letters", 10));

        Check.That(KeyboardFitCap.EffectiveKeySizeDip(layout, 90, 1000)).IsCloseTo(90.0, 1e-9);
        Check.That(KeyboardFitCap.EffectiveKeySizeDip(layout, 140, 1000)).IsCloseTo(96.0, 1e-9);
    }

    [Fact]
    public void MaxKeySize_UsesTheWidestRowOfEveryPage_NotOnlyTheDefaultOne()
    {
        // The default page is the narrower one on purpose: an implementation that capped from
        // layout.DefaultSubLayout (or from the current page) would return the larger 96 DIP and
        // let the 14-key function page overflow its work area at a page switch.
        var layout = Layout(("letters", 10), ("function", 14));

        var max = KeyboardFitCap.MaxKeySizeDip(layout, availableWidthDip: 1000);

        // 14 plain keys: 14 * (size + 4) = 1000 -> size 67.428...
        Check.That(max).IsCloseTo((1000.0 - 56) / 14, 1e-9);
        Check.That(max).IsStrictlyLessThan(96.0);
        Check.That(KeyboardFitCap.PageWidthDip(layout.SubLayouts["function"], max)).IsCloseTo(1000.0, 1e-9);
        Check.That(KeyboardFitCap.PageWidthDip(layout.SubLayouts["letters"], max)).IsStrictlyLessThan(1000.0);
    }

    [Fact]
    public void MaxKeySize_OnAWorkAreaTooSmallForTheKeys_IsZero_AndDoesNotThrow()
    {
        // A 10-DIP work area cannot hold the ten 4-DIP slots even at size 0 (40 DIP). The rule is
        // "never throw, never negative": a Math.Clamp-style inverted range here would kill the
        // process on a small or high-DPI screen.
        var layout = Layout(("letters", 10));

        var max = KeyboardFitCap.MaxKeySizeDip(layout, availableWidthDip: 10);

        Check.That(max).IsEqualTo(0.0);
        Check.That(KeyboardFitCap.EffectiveKeySizeDip(layout, 140, 10)).IsEqualTo(0.0);
    }
}
