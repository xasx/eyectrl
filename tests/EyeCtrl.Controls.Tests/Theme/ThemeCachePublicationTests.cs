namespace EyeCtrl.Controls.Tests.Theme;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using EyeCtrl.Controls.Theme;
using NFluent;
using Xunit;

/// <summary>
/// ThemeBase caches brushes and the FontFamily on first access, and Tokens exposes one
/// theme process-wide, so concurrent first access must publish exactly ONE instance per
/// slot. A plain <c>??=</c> does not: two threads can both see null, both build, and the
/// loser's brush is returned to its caller without ever being stored.
///
/// That is observable, not academic — SettingsRenderMappingTests pins the settings label
/// brush by reference identity, and it flaked (once in five runs, cold JIT) with two
/// equal-but-distinct #FA1A1A2E brushes.
///
/// Each case needs a theme whose slots are still cold, so it derives one rather than using
/// a registry singleton: another test in this assembly may already have warmed those.
/// Threads are explicit rather than pool tasks — the release needs all participants running
/// at once, and pool injection delay under a loaded suite would defeat that.
///
/// Measured against the pre-fix <c>??=</c> form: all three cases failed on round 0 with 7-8
/// distinct instances, in three consecutive runs. These are real guards, not decoration —
/// the window is wide because the build (allocate + Freeze) sits between the null check and
/// the store, which is precisely what <c>??=</c> does and CompareExchange does not.
/// </summary>
public class ThemeCachePublicationTests
{
    private static readonly int Racers = Math.Max(4, Math.Min(8, Environment.ProcessorCount));

    // Round 0 caught the pre-fix form every time; the rest are margin for a slower machine.
    private const int Rounds = 8;

    [Fact]
    public void ConcurrentFirstAccess_PublishesOneBrushPerSlot()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var theme = new ColdTheme();
            var seen = RaceFor(() => theme.BrushSurfaceDark);

            Check.WithCustomMessage($"round {round}: threads saw {Distinct(seen)} distinct brushes")
                 .That(Distinct(seen)).IsEqualTo(1);
            Check.WithCustomMessage(
                    $"round {round}: a racer was handed a brush that was never stored, so a "
                    + "later read returns a different instance.")
                 .That(theme.BrushSurfaceDark).IsSameReferenceAs(seen[0]);
            Check.That(seen[0]!.IsFrozen).IsTrue();
        }
    }

    [Fact]
    public void ConcurrentFirstAccess_PublishesOneFontFamily()
    {
        for (var round = 0; round < Rounds; round++)
        {
            var theme = new ColdTheme();
            var seen = RaceFor(() => theme.FontStandard);

            Check.WithCustomMessage($"round {round}: threads saw {Distinct(seen)} distinct families")
                 .That(Distinct(seen)).IsEqualTo(1);
            Check.That(theme.FontStandard).IsSameReferenceAs(seen[0]);
        }
    }

    [Fact]
    public void ConcurrentFirstAccess_OfTheDerivedDimBrush_PublishesOneInstance()
    {
        // BrushAccentDim10 is the one derived slot — built from Accent at 10%, not from a
        // colour of its own — so it takes the same publication path by a different route.
        for (var round = 0; round < Rounds; round++)
        {
            var theme = new ColdTheme();
            var seen = RaceFor(() => theme.BrushAccentDim10);

            Check.WithCustomMessage($"round {round}: threads saw {Distinct(seen)} distinct brushes")
                 .That(Distinct(seen)).IsEqualTo(1);
            Check.That(theme.BrushAccentDim10).IsSameReferenceAs(seen[0]);
            Check.That(seen[0]!.Opacity).IsEqualTo(0.10);
        }
    }

    /// <summary>
    /// Reads <paramref name="read"/> from <see cref="Racers"/> threads released as close to
    /// simultaneously as the machine allows, and returns what each one saw.
    ///
    /// Release is a spin on a volatile flag, not a <see cref="Barrier"/>: the unsynchronised
    /// window inside the cache is only an allocate-and-Freeze wide, and a barrier's
    /// kernel-assisted release skews threads by more than that — measured against a
    /// deliberately racy cache, a barrier caught nothing while this catches it every run.
    /// </summary>
    private static T?[] RaceFor<T>(Func<T> read) where T : class
    {
        var seen = new T?[Racers];
        var threads = new Thread[Racers];
        var ready = 0;
        var go = false;

        for (var i = 0; i < Racers; i++)
        {
            var slot = i;
            threads[slot] = new Thread(() =>
            {
                Interlocked.Increment(ref ready);
                while (!Volatile.Read(ref go))
                    Thread.SpinWait(1);

                seen[slot] = read();
            })
            { IsBackground = true };
        }

        foreach (var t in threads)
            t.Start();

        var spun = new SpinWait();
        while (Volatile.Read(ref ready) < Racers)
            spun.SpinOnce();

        Volatile.Write(ref go, true);

        foreach (var t in threads)
        {
            Check.WithCustomMessage("a racer thread did not finish")
                 .That(t.Join(TimeSpan.FromSeconds(30))).IsTrue();
        }

        return seen;
    }

    /// <summary>Counts distinct instances by reference — brushes with equal colours are not equal here.</summary>
    private static int Distinct<T>(T?[] seen) where T : class
    {
        var unique = new List<T?>();
        foreach (var item in seen)
        {
            if (!unique.Any(u => ReferenceEquals(u, item)))
                unique.Add(item);
        }

        return unique.Count;
    }

    /// <summary>
    /// A theme with cold caches. Values are Midnight's — this test is about publication,
    /// not palette — but the instance is fresh, so every slot starts null.
    /// </summary>
    private sealed class ColdTheme : ThemeBase
    {
        private static readonly ITheme Values = MidnightTheme.Instance;

        public override string Name => "Cold";

        public override Color SurfaceDark => Values.SurfaceDark;
        public override Color SurfaceMedium => Values.SurfaceMedium;
        public override Color SurfaceLight => Values.SurfaceLight;
        public override Color SurfaceKey => Values.SurfaceKey;
        public override Color SurfaceKeyModifier => Values.SurfaceKeyModifier;
        public override Color SurfaceKeySwitch => Values.SurfaceKeySwitch;
        public override Color SurfaceKeyActive => Values.SurfaceKeyActive;
        public override Color SurfacePrediction => Values.SurfacePrediction;
        public override Color BorderSubtle => Values.BorderSubtle;
        public override Color BorderFocus => Values.BorderFocus;
        public override Color TextPrimary => Values.TextPrimary;
        public override Color TextSecondary => Values.TextSecondary;
        public override Color TextDim => Values.TextDim;
        public override Color MarkerMove => Values.MarkerMove;
        public override Color Accent => Values.Accent;
        public override Color StrokeHalo => Values.StrokeHalo;
        public override Color AccentSuccess => Values.AccentSuccess;
        public override Color AccentWarning => Values.AccentWarning;
        public override Color AccentDanger => Values.AccentDanger;
        public override Color ToggleOn => Values.ToggleOn;
        public override Color ToggleOff => Values.ToggleOff;
        public override Color ModalBackdrop => Values.ModalBackdrop;

        public override double BorderThicknessStandard => Values.BorderThicknessStandard;

        public override double CornerRadiusSmall => Values.CornerRadiusSmall;
        public override double CornerRadiusStandard => Values.CornerRadiusStandard;
        public override double CornerRadiusLarge => Values.CornerRadiusLarge;
        public override double CornerRadiusCircle => Values.CornerRadiusCircle;

        public override string FontFamilyStandard => Values.FontFamilyStandard;
        public override FontWeight FontWeightMedium => Values.FontWeightMedium;
        public override FontWeight FontWeightStrong => Values.FontWeightStrong;

        public override double FontSizeBody => Values.FontSizeBody;
        public override double FontSizeBodyLarge => Values.FontSizeBodyLarge;
        public override double FontSizeSubtitle => Values.FontSizeSubtitle;
        public override double FontSizeTitle => Values.FontSizeTitle;
        public override double FontSizeDisplay => Values.FontSizeDisplay;
    }
}
