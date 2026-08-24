namespace EyeCtrl.Controls.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// The two per-frame allocations that iterating an <see cref="IDwellComposite"/>'s children used
/// to cost, and the shape that removes both.
///
/// <c>SettingsWindow.UpdateAllTargetPositions</c> runs once per gaze frame and walks every
/// attached composite's <see cref="IDwellComposite.Children"/>. Two separate costs lived there:
///
/// 1. THE COLLECTION. <see cref="DwellSlider"/> was written <c>Children =&gt; [_minus, _plus]</c>,
///    a collection expression that materialises a fresh instance on every get.
///    <see cref="DwellComboBox"/> and <see cref="DwellColorPicker"/> return their backing
///    <c>List&lt;DwellButton&gt;</c> field, which reaches the property type by a covariant
///    reference conversion and allocates nothing — they were already correct.
/// 2. THE ENUMERATOR. The call site's <c>foreach</c> over an interface-typed sequence boxed an
///    enumerator per composite per frame, for a cached array exactly as much as for a cached
///    List. Fixed by making <see cref="IDwellComposite.Children"/> an
///    <see cref="IReadOnlyList{T}"/> and indexing it.
///
/// Both matter because a Gen0 collection suspends the gaze thread.
///
/// Cost 1 is gated by reference identity, NOT by a byte count:
/// <c>GC.GetAllocatedBytesForCurrentThread</c> would be the direct measurement, but .NET 10's
/// escape analysis is free to stack-allocate a short-lived array and report zero bytes for the
/// very form the gate exists to reject — a gate that passes on the broken code. Identity cannot
/// be faked. Cost 2 IS measured in bytes, and measured against the old <c>foreach</c> shape in
/// the same run, so the test states its own premise rather than asserting a zero that might mean
/// nothing.
///
/// Scope, so a reader does not over-read the zero: these cover the composite-children walk only.
/// <c>UpdateAllTargetPositions</c> also allocates four <c>Action</c> delegates per frame at its
/// <c>UpdateButtonPositions</c> call sites (each lambda captures <c>this</c>, so Roslyn cannot
/// cache it) — a separate finding, not covered here.
/// </summary>
public class DwellCompositeChildrenStabilityTests
{
    [StaFact]
    public void DwellSlider_HandsOutTheSameChildCollectionOnEveryGet()
        => AssertStable(NewSlider(), expectedChildren: 2);   // minus, plus

    [StaFact]
    public void DwellComboBox_HandsOutTheSameChildCollectionOnEveryGet()
        => AssertStable(NewCombo(), expectedChildren: 2);    // one option button per code

    [StaFact]
    public void DwellColorPicker_HandsOutTheSameChildCollectionOnEveryGet()
        => AssertStable(NewPicker(), expectedChildren: 2);   // one swatch per preset

    [StaFact]
    public void DwellToggle_HandsOutTheSameChildCollectionOnEveryGet()
        => AssertStable(NewToggle(), expectedChildren: 1);   // the switch is the only target

    /// <summary>
    /// A composite added later would otherwise be free to reintroduce the collection expression
    /// with the whole suite green. Set equality against the assembly's own implementers, so a
    /// new composite fails here until it gets a case above.
    /// </summary>
    [Fact]
    public void EveryCompositeInTheAssembly_IsCoveredByACaseAbove()
    {
        var implementers = typeof(IDwellComposite).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IDwellComposite).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        string[] covered = ["DwellColorPicker", "DwellComboBox", "DwellSlider", "DwellToggle"];

        Check.WithCustomMessage(
                "IDwellComposite implementers and the cases in this class have diverged."
                + " in assembly: " + string.Join(", ", implementers)
                + " | covered: " + string.Join(", ", covered)
                + ". A composite with no case here can allocate a fresh Children collection on"
                + " every gaze frame and nothing will notice.")
            .That(implementers).ContainsExactly(covered);
    }

    /// <summary>
    /// The reflection census above sees one assembly — the one this suite references. An
    /// implementer in EyeCtrl.App (or any other project) would be invisible to it, so the same
    /// set is checked against the source of every project. None exist outside Controls today;
    /// this is what notices the first one that does.
    ///
    /// Its own test rather than another assertion in the census: NFluent fails fast, and a
    /// reflection failure would abort before this ever ran.
    ///
    /// <para>
    /// <c>LabelledSettingsRow</c> is the one implementer outside this project: a private nested
    /// class in <c>SettingsWindow</c> that wraps a heading-less composite in the row carrying the
    /// setting's name. It cannot be covered by a case above — this suite does not reference
    /// EyeCtrl.App, and the type is private — so its Children stability is pinned where it can be
    /// reached, by
    /// <c>EyeCtrl.App.Tests.Settings.SettingsRowLayoutTests.ALabelledRow_ForwardsItsInnerCompositesCachedChildren</c>.
    /// It forwards the inner composite's cached list and allocates nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void NoProjectOutsideControls_ImplementsIDwellComposite()
    {
        var declared = Directory
            .EnumerateFiles(Path.Combine(RepoRoot().FullName, "src"), "*.cs",
                SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(p => Regex.Matches(
                File.ReadAllText(p),
                @"\bclass\s+(\w+)\s*:[^{]*\bIDwellComposite\b")
                .Select(m => m.Groups[1].Value))
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        // LabelledSettingsRow is covered in EyeCtrl.App.Tests, not by a case above — see the
        // remarks on this method for why it cannot be.
        string[] covered =
            ["DwellColorPicker", "DwellComboBox", "DwellSlider", "DwellToggle",
             "LabelledSettingsRow"];

        Check.WithCustomMessage(
                "IDwellComposite implementers in src/ and the cases in this class have diverged."
                + " in source: " + string.Join(", ", declared)
                + " | covered: " + string.Join(", ", covered)
                + ". An implementer this suite cannot reflect over still runs on the gaze path;"
                + " give it a case above (and check its Children is a cached field).")
            .That(declared).ContainsExactly(covered);
    }

    /// <summary>
    /// The indexed walk allocates nothing; the <c>foreach</c> it replaced allocates on every
    /// pass. Both are measured here so the zero above is meaningful — a lone "indexed is zero"
    /// assertion would also pass if enumerating had never cost anything in the first place.
    /// </summary>
    [StaFact]
    public void WalkingChildrenByIndex_AllocatesNothing_WhileForeachAllocatesPerComposite()
    {
        IDwellComposite[] composites = [NewSlider(), NewCombo(), NewPicker()];
        const int Frames = 100;

        // Settle tiered JIT before measuring: promotion happens around 30 calls, and an OSR
        // transition inside the measured window would show up as noise in the byte delta.
        for (int i = 0; i < 300; i++) { WalkByIndex(composites); WalkByForeach(composites); }

        var t0 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < Frames; f++) WalkByIndex(composites);
        var indexed = GC.GetAllocatedBytesForCurrentThread() - t0;

        var t1 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < Frames; f++) WalkByForeach(composites);
        var byForeach = GC.GetAllocatedBytesForCurrentThread() - t1;

        Check.WithCustomMessage(
                $"the indexed walk allocated {indexed} bytes over {Frames} frames across"
                + $" {composites.Length} composites. Something on this path is allocating again:"
                + " either a composite rebuilds its Children per get, or Children is no longer"
                + " indexable and this walk fell back to enumerating.")
            .That(indexed).IsEqualTo(0L);

        Check.WithCustomMessage(
                $"the foreach walk allocated {byForeach} bytes, not more than zero — enumerating"
                + " an interface-typed sequence no longer boxes on this runtime, so the reason"
                + " IDwellComposite.Children is an IReadOnlyList (and the reason SettingsWindow"
                + " indexes it) no longer holds. Re-verify before trusting the comment on"
                + " IDwellComposite.Children; do not just delete this assertion.")
            .That(byForeach).IsStrictlyGreaterThan(0L);
    }

    /// <summary>
    /// Two ratchets on the fix for cost 2, because each hole is invisible from the other side:
    /// widening <see cref="IDwellComposite.Children"/> back to <see cref="IEnumerable{T}"/> would
    /// force the call site back to <c>foreach</c>, and leaving the type alone while writing a
    /// <c>foreach</c> at the call site compiles and boxes just the same.
    /// </summary>
    [Fact]
    public void Children_IsAnIndexableType()
    {
        var prop = typeof(IDwellComposite).GetProperty(nameof(IDwellComposite.Children))!;
        Check.WithCustomMessage(
                $"IDwellComposite.Children is {prop.PropertyType.Name}. It must stay an"
                + " IReadOnlyList<DwellControl>: an IEnumerable leaves the per-frame loop no"
                + " choice but to foreach, which boxes an enumerator per composite per frame.")
            .That(prop.PropertyType).IsEqualTo(typeof(IReadOnlyList<DwellControl>));
    }

    /// <summary>
    /// Separate from the type pin, because the type does not imply the call site: IReadOnlyList
    /// only makes indexing POSSIBLE, and a foreach over it still compiles and still boxes. Kept a
    /// separate test rather than a second assertion so the revert-check can exercise both — a
    /// fail-fast abort on the pin would otherwise hide whether this half works at all.
    /// </summary>
    [Fact]
    public void TheFrameLoop_IndexesChildren_RatherThanForeachingThem()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"));

        Check.WithCustomMessage(
                "SettingsWindow foreaches a composite's Children again. IReadOnlyList does not"
                + " prevent that — it only makes indexing possible — so the call site has to be"
                + " held separately. Index it: for (int i = 0; i < children.Count; i++).")
            .That(Regex.IsMatch(source, @"foreach\s*\([^)]*\bin\s+\w+\.Children\s*\)")).IsFalse();

        Check.WithCustomMessage(
                "SettingsWindow no longer contains the indexed composite-children walk this test"
                + " exists to protect. If the loop moved, move this assertion with it — do not"
                + " leave the gate pointing at a file that no longer does the work.")
            .That(source).Contains("for (int i = 0; i < children.Count; i++)");
    }

    private static void AssertStable(IDwellComposite composite, int expectedChildren)
    {
        var first = composite.Children;
        var second = composite.Children;

        Check.WithCustomMessage(
                $"{composite.GetType().Name}.Children returned a different object on the second"
                + " get, so it allocates once per composite per gaze frame in"
                + " SettingsWindow.UpdateAllTargetPositions. Cache the collection in a readonly"
                + " field and return that field — a collection expression (=> [a, b]) creates a"
                + " new one each time.")
            .That(ReferenceEquals(first, second)).IsTrue();

        Check.WithCustomMessage(
                $"{composite.GetType().Name} exposes {first.Count} children, expected"
                + $" {expectedChildren} — caching must not change what the composite hands out.")
            .That(first.Count).IsEqualTo(expectedChildren);
    }

    /// <summary>What SettingsWindow.UpdateAllTargetPositions does, minus the WPF calls.</summary>
    private static int WalkByIndex(IDwellComposite[] composites)
    {
        int seen = 0;
        for (int c = 0; c < composites.Length; c++)
        {
            var children = composites[c].Children;
            for (int i = 0; i < children.Count; i++)
                if (children[i] is not null) seen++;
        }

        return seen;
    }

    /// <summary>What it used to do. Kept only as the measured baseline above.</summary>
    private static int WalkByForeach(IDwellComposite[] composites)
    {
        int seen = 0;
        for (int c = 0; c < composites.Length; c++)
        {
            IEnumerable<DwellControl> children = composites[c].Children;
            foreach (var child in children)
                if (child is not null) seen++;
        }

        return seen;
    }

    private static DwellSlider NewSlider()
    {
        var value = 5.0;
        return new DwellSlider(
            new FakeRegistrar(), "sl", "Speed", 0, 10, 1, 0,
            () => value, v => value = v,
            DwellCategory.Control, 50, 800);
    }

    private static DwellComboBox NewCombo()
    {
        var code = "a";
        return new DwellComboBox(
            new FakeRegistrar(), "cb", "Mode",
            ["Alpha", "Beta"], ["a", "b"],
            () => code, v => code = v,
            DwellCategory.Control, 50, 800);
    }

    private static DwellColorPicker NewPicker()
    {
        var hex = "#A5F3FC";
        return new DwellColorPicker(
            new FakeRegistrar(), "cp", "Colour", () => hex, v => hex = v,
            DwellCategory.Control, 50, 800,
            presets: [(hex, "Test"), ("#F87171", "Other")]);
    }

    private static DwellToggle NewToggle()
    {
        var on = false;
        return new DwellToggle(
            new FakeRegistrar(), "tg", "Start on Windows Boot",
            () => on, v => on = v,
            DwellCategory.Control, 50, 800);
    }

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(DwellCompositeChildrenStabilityTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;

        Check.WithCustomMessage("Could not locate the repo root (EyeCtrl.slnx)")
            .That(dir).IsNotNull();
        return dir!;
    }

    private class FakeRegistrar : IDwellRegistrar
    {
        public void Register(string id, DwellCategory category, double radius, int dwellMs, Action onActivated) { }
        public void Unregister(string id, DwellCategory category) { }
        public void UpdatePosition(string id, DwellCategory category, PhysicalPoint point) { }
        public double GetProgress(string id, DwellCategory category) => 0;
    }
}
