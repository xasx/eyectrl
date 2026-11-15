namespace EyeCtrl.Platform.Tests.Icons;

using System.Diagnostics;
using System.Windows.Media.Imaging;
using EyeCtrl.Platform.Icons;

/// <summary>
/// The provider end to end: the per-WINDOW contract, the guarantee that an icon always exists, and
/// the two real measurements the ticket asks for (first open vs cached open, and a real shell
/// extraction from this test process's own executable — reading an icon touches nothing on the
/// owner's desktop).
/// </summary>
public class Win32AppIconProviderTests
{
    private const string FrameAumidOne = "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App";
    private const string FrameAumidTwo = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";
    private const uint FrameHostProcess = 777;

    private readonly ITestOutputHelper _output;

    public Win32AppIconProviderTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TwoWindowsOfOneFrameHostProcess_GetTheirOwnAppsIcon()
    {
        // The ticket's mutant (a) in behavioural form: if the provider resolved (or cached) per
        // PROCESS, both Store apps would share ApplicationFrameHost's single icon.
        var identity = new FakeIdentity();
        var shell = new FakeShellImages();
        var first = new IntPtr(0x4001);
        var second = new IntPtr(0x4002);
        foreach (var (hwnd, aumid) in new[] { (first, FrameAumidOne), (second, FrameAumidTwo) })
        {
            identity.ClassNames[hwnd] = WindowIdentityFacts.FrameHostClassName;
            identity.ExecutablePaths[FrameHostProcess] = @"C:\Windows\System32\ApplicationFrameHost.exe";
            identity.AppUserModelIds[hwnd] = aumid;
        }

        var provider = new Win32AppIconProvider(
        [
            new PackagedAppIconSource(identity, shell),
            new ExecutableIconSource(identity, shell),
            new WindowIconSource(new FakeWindowIconReader(null)),
            new DefaultGlyphIconSource(),
        ]);

        var firstIcon = provider.GetIcon(first, FrameHostProcess);
        var secondIcon = provider.GetIcon(second, FrameHostProcess);

        Check.WithCustomMessage("both Store apps got the SAME icon instance").That(ReferenceEquals(secondIcon, firstIcon)).IsFalse();
        Check.That(shell.Requests.Select(r => r.ParsingName)).ContainsExactly(
            "shell:AppsFolder\\" + FrameAumidOne, "shell:AppsFolder\\" + FrameAumidTwo);
    }

    [Fact]
    public void NoSourceHasAnIcon_StillYieldsTheNeutralGlyph_NeverAnEmptyHole()
    {
        var provider = new Win32AppIconProvider(
        [
            new FakeIconSource(AppIconSourceKind.PackagedApp),
            new FakeIconSource(AppIconSourceKind.Executable),
            new FakeIconSource(AppIconSourceKind.Window),
        ]);

        var icon = provider.GetIcon(new IntPtr(0x4003), 778);

        Check.That(icon).IsSameReferenceAs(DefaultGlyphIconSource.Glyph);
    }

    [Fact]
    public void ABrokenSource_DoesNotBreakTheOpen()
    {
        var exeIcon = IconFakes.Image(71);
        var provider = new Win32AppIconProvider(
        [
            new FakeIconSource(AppIconSourceKind.PackagedApp, throws: true),
            new FakeIconSource(AppIconSourceKind.Executable, exeIcon),
        ]);

        Check.That(provider.GetIcon(new IntPtr(0x4004), 779)).IsSameReferenceAs(exeIcon);
    }

    /// <summary>
    /// The ticket's real-extraction proof: this test process's own executable resolves through the
    /// real shell path to a bitmap of at least 64 px — the 80 physical px a 40 DIP icon needs at
    /// 200 % must not be an upscale of a 32 px image. No window is read (handle 0 → the window
    /// source declines), and nothing on the desktop is touched.
    /// </summary>
    [Fact]
    public void ThisTestExecutable_ResolvesToAnIconOfAtLeast64Px()
    {
        var provider = new Win32AppIconProvider();

        var icon = provider.GetIcon(IntPtr.Zero, (uint)Environment.ProcessId);

        Check.That(icon).IsNotNull();
        var bitmap = icon as BitmapSource;
        Check.That(bitmap).IsNotNull();
        _output.WriteLine(
            $"own exe icon: {bitmap!.PixelWidth}x{bitmap.PixelHeight} {bitmap.Format}, "
            + $"dpi {bitmap.DpiX:0}x{bitmap.DpiY:0}, frozen {bitmap.IsFrozen}");
        Check.WithCustomMessage(
                $"The shell returned {bitmap!.PixelWidth}x{bitmap.PixelHeight}: a 40 DIP icon at 200 % is "
                + "80 physical px, so the source must be at least 64 px or it is drawn upscaled.")
            .That(bitmap.PixelWidth).IsGreaterOrEqualThan(64);
        Check.That(bitmap.PixelHeight).IsGreaterOrEqualThan(64);
    }

    /// <summary>
    /// The measured cost the report quotes: one cold resolution (exe path + shell image + WPF
    /// conversion) against the cached path a second open pays. Printed, not asserted as a timing
    /// budget — the deterministic "no re-extraction" claims live in the source tests; a hard
    /// millisecond bound would only make a loaded runner flaky.
    /// </summary>
    [Fact]
    public void IconResolutionCost_FirstOpenVersusCachedOpen_IsMeasured()
    {
        var provider = new Win32AppIconProvider();
        var self = (uint)Environment.ProcessId;

        // Warm the WPF/COM machinery with an unrelated first touch, so the printed "first open" is
        // the icon path, not one-time runtime initialization.
        _ = provider.GetIcon(IntPtr.Zero, self);

        var coldProvider = new Win32AppIconProvider();
        var cold = Stopwatch.StartNew();
        _ = coldProvider.GetIcon(IntPtr.Zero, self);
        cold.Stop();

        const int Samples = 20;
        var cached = new List<double>(Samples);
        for (var i = 0; i < Samples; i++)
        {
            var watch = Stopwatch.StartNew();
            _ = provider.GetIcon(IntPtr.Zero, self);
            watch.Stop();
            cached.Add(watch.Elapsed.TotalMilliseconds);
        }

        cached.Sort();
        var median = cached[Samples / 2];
        var perOpen = cold.Elapsed.TotalMilliseconds + (median * 20);   // a 20-window session
        _output.WriteLine(
            $"icon cost: first resolution {cold.Elapsed.TotalMilliseconds:0.000} ms; "
            + $"cached median {median:0.0000} ms (min {cached[0]:0.0000}, max {cached[^1]:0.0000}); "
            + $"20-window session estimate {perOpen:0.00} ms");

        Check.WithCustomMessage(
                $"a cache hit took {median:0.000} ms: the second open must not re-extract an icon")
            .That(median).IsStrictlyLessThan(5.0);
    }
}
