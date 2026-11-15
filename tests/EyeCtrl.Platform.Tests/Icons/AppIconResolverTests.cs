namespace EyeCtrl.Platform.Tests.Icons;

using System.Windows.Media;
using EyeCtrl.Platform.Icons;

/// <summary>
/// S3-B's source-order decision as a pure unit: UWP icon → exe path → the window's own icon →
/// the neutral default glyph, first success wins. The order belongs to the resolver, so a
/// differently ordered source list (DI, a test's shuffle) cannot change which icon wins, and no
/// source can throw its way out of the opening path.
/// </summary>
public class AppIconResolverTests
{
    private static readonly IconRequest AnyWindow = new(new IntPtr(0x1234), 4321);

    [Fact]
    public void EverySourceIsAskedInTheCanonicalOrder_WhateverOrderTheListHas()
    {
        var asked = new List<AppIconSourceKind>();
        var sources = new IAppIconSource[]
        {
            new RecordingSource(AppIconSourceKind.Default, asked),
            new RecordingSource(AppIconSourceKind.Window, asked),
            new RecordingSource(AppIconSourceKind.Executable, asked),
            new RecordingSource(AppIconSourceKind.PackagedApp, asked),
        };

        var (kind, icon) = AppIconResolver.Resolve(sources, AnyWindow);

        Check.That(asked)
            .ContainsExactly(AppIconSourceKind.PackagedApp, AppIconSourceKind.Executable,
                AppIconSourceKind.Window, AppIconSourceKind.Default);
        Check.That(kind).IsEqualTo(AppIconSourceKind.Default);
        Check.That(icon).IsSameReferenceAs(DefaultGlyphIconSource.Glyph);

        // The canonical order is also a published value, not only an emergent behaviour.
        Check.That(AppIconResolver.Order).ContainsExactly(
            AppIconSourceKind.PackagedApp, AppIconSourceKind.Executable,
            AppIconSourceKind.Window, AppIconSourceKind.Default);
    }

    [Fact]
    public void TheStoreAppsIconWins_AndNoLaterSourceIsAsked()
    {
        var storeIcon = IconFakes.Image(11);
        var packaged = new FakeIconSource(AppIconSourceKind.PackagedApp, storeIcon);
        var executable = new FakeIconSource(AppIconSourceKind.Executable, IconFakes.Image(12));
        var window = new FakeIconSource(AppIconSourceKind.Window, IconFakes.Image(13));

        var (kind, icon) = AppIconResolver.Resolve([packaged, executable, window], AnyWindow);

        Check.That(kind).IsEqualTo(AppIconSourceKind.PackagedApp);
        Check.That(icon).IsSameReferenceAs(storeIcon);
        Check.That(executable.Calls).IsEqualTo(0);
        Check.That(window.Calls).IsEqualTo(0);
    }

    [Fact]
    public void TheExecutablesIconWins_WhenTheStoreSourceDeclines()
    {
        var exeIcon = IconFakes.Image(21);
        var packaged = new FakeIconSource(AppIconSourceKind.PackagedApp);
        var executable = new FakeIconSource(AppIconSourceKind.Executable, exeIcon);

        var (kind, icon) = AppIconResolver.Resolve([packaged, executable], AnyWindow);

        Check.That(kind).IsEqualTo(AppIconSourceKind.Executable);
        Check.That(icon).IsSameReferenceAs(exeIcon);
        Check.That(packaged.Calls).IsEqualTo(1);
    }

    [Fact]
    public void TheWindowsOwnIconWins_WhenTheFirstTwoDecline()
    {
        var windowIcon = IconFakes.Image(31);
        var window = new FakeIconSource(AppIconSourceKind.Window, windowIcon);

        var (kind, icon) = AppIconResolver.Resolve(
            [new FakeIconSource(AppIconSourceKind.PackagedApp), new FakeIconSource(AppIconSourceKind.Executable), window],
            AnyWindow);

        Check.That(kind).IsEqualTo(AppIconSourceKind.Window);
        Check.That(icon).IsSameReferenceAs(windowIcon);
    }

    [Fact]
    public void TheDefaultSourceIsTheLastResort()
    {
        var defaultIcon = IconFakes.Image(41);
        var fallback = new FakeIconSource(AppIconSourceKind.Default, defaultIcon);

        var (kind, icon) = AppIconResolver.Resolve(
            [new FakeIconSource(AppIconSourceKind.PackagedApp), new FakeIconSource(AppIconSourceKind.Executable),
             new FakeIconSource(AppIconSourceKind.Window), fallback],
            AnyWindow);

        Check.That(kind).IsEqualTo(AppIconSourceKind.Default);
        Check.That(icon).IsSameReferenceAs(defaultIcon);
        Check.That(fallback.Calls).IsEqualTo(1);
    }

    [Fact]
    public void AThrowingSource_FallsThroughToTheNext_AndNeverEscapes()
    {
        var windowIcon = IconFakes.Image(51);
        var icon = AppIconResolver.Resolve(
            [new FakeIconSource(AppIconSourceKind.Executable, throws: true),
             new FakeIconSource(AppIconSourceKind.Window, windowIcon)],
            AnyWindow);

        Check.WithCustomMessage("A broken extraction is decoration: it must never break the switcher's open.")
            .That(icon.Kind).IsEqualTo(AppIconSourceKind.Window);
        Check.That(icon.Icon).IsSameReferenceAs(windowIcon);
    }

    [Fact]
    public void WithoutAnySource_TheRealDefaultGlyphStillGuaranteesAnIcon()
    {
        var (kind, icon) = AppIconResolver.Resolve([], AnyWindow);

        Check.That(kind).IsEqualTo(AppIconSourceKind.Default);
        Check.That(icon).IsSameReferenceAs(DefaultGlyphIconSource.Glyph);
        Check.That(icon).IsNotNull();
    }

    /// <summary>Records that it was asked, and declines — for the order pin.</summary>
    private sealed class RecordingSource : IAppIconSource
    {
        private readonly List<AppIconSourceKind> _asked;

        internal RecordingSource(AppIconSourceKind kind, List<AppIconSourceKind> asked)
        {
            Kind = kind;
            _asked = asked;
        }

        public AppIconSourceKind Kind { get; }

        public ImageSource? TryGetIcon(IconRequest request)
        {
            _asked.Add(Kind);
            return null;
        }
    }
}
