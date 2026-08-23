namespace EyeCtrl.App.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using EyeCtrl.Core.Processing;
using NFluent;
using Xunit;

/// <summary>
/// <c>SettingsWindow.UpdateAllTargetPositions</c> runs once per gaze frame, so nothing in it may
/// allocate: a Gen0 collection suspends the gaze thread.
///
/// It used to hand <c>UpdateButtonPositions</c> a callback —
/// <c>(id, pos) =&gt; _viewModel.UpdatePosition(id, DwellCategory.Tab, pos)</c> — at four call
/// sites. That lambda captures <c>this</c> (it reads <c>_viewModel</c>), and Roslyn only caches
/// lambdas that capture nothing, so each site allocated a fresh delegate every frame. All four
/// differed by nothing but the category, so the parameter became a <see cref="DwellCategory"/>
/// and the delegate disappeared along with the indirection.
///
/// SettingsWindow cannot be constructed in this suite (it is a WPF window, and building one is
/// exactly what the Category=UI tests are for), so the real method is held by reading its source.
/// The runtime claim the fix rests on — that a <c>this</c>-capturing lambda argument allocates on
/// every call — is measured separately below against a mirror of the same shape, so the ratchet
/// is not resting on an unverified belief about the compiler.
/// </summary>
public class SettingsFrameLoopAllocationTests
{
    private const int Frames = 100;

    [Fact]
    public void TheFrameLoop_PassesCategoriesNotCallbacks()
    {
        var source = ReadSettingsWindow();

        Check.WithCustomMessage(
                "UpdateButtonPositions takes a DwellCategory, not an Action. Restoring the"
                + " callback parameter puts a captured-this delegate allocation back on the"
                + " per-gaze-frame path at every call site.")
            .That(Regex.IsMatch(
                source,
                @"private void UpdateButtonPositions\(\s*Dictionary<string, Border> borders,\s*"
                + @"DwellCategory category\s*\)"))
            .IsTrue();

        Check.WithCustomMessage(
                "a lambda is being passed to UpdateButtonPositions again — that is the exact"
                + " allocation this gate exists to keep out. Pass the DwellCategory instead.")
            .That(Regex.IsMatch(source, @"UpdateButtonPositions\([^)]*=>"))
            .IsFalse();
    }

    /// <summary>
    /// The general form of the same rule: no lambda anywhere in the per-frame method, not just at
    /// the call sites this fix touched. Anything with a <c>=&gt;</c> in that body is either a
    /// closure (allocates per frame) or a switch/expression form that a reader will mistake for
    /// one — both worth stopping to look at.
    /// </summary>
    [Fact]
    public void ThePerFrameMethod_ContainsNoLambdaAtAll()
    {
        var source = ReadSettingsWindow();

        const string Signature = "private void UpdateAllTargetPositions()";
        var start = source.IndexOf(Signature, StringComparison.Ordinal);
        Check.WithCustomMessage(
                $"'{Signature}' is gone from SettingsWindow. If the per-frame loop was renamed or"
                + " moved, point this gate at its new home rather than deleting it.")
            .That(start).IsNotEqualTo(-1);

        // To the next member declared at class indent — the file declares every member as
        // "\n    private "/"\n    public ", so this slices the method body without brace counting.
        var rest = source[(start + Signature.Length)..];
        var end = Regex.Match(rest, @"\r?\n    (private|public|internal|protected) ");
        var body = end.Success ? rest[..end.Index] : rest;

        Check.WithCustomMessage(
                "UpdateAllTargetPositions now contains a '=>'. If it is a lambda, it allocates a"
                + " delegate (and a display class, if it captures locals) on every gaze frame —"
                + " hoist it to a field or replace it with a plain argument. If it is a switch arm"
                + " or expression body that genuinely allocates nothing, widen this gate on"
                + " purpose and say so here.")
            .That(body).Not.Contains("=>");
    }

    /// <summary>
    /// The measured basis for the fix. Both shapes run over an EMPTY border map, so the only
    /// difference between them is how the category reaches the helper — not any work done per
    /// border.
    /// </summary>
    [Fact]
    public void PassingACapturingLambdaPerFrame_Allocates_WhileACategoryArgumentDoesNot()
    {
        var mirror = new FrameLoopMirror();

        // Settle tiered JIT before measuring: promotion happens around 30 calls, and a tier
        // transition inside a measured window would show up as noise in the byte delta.
        for (int i = 0; i < 300; i++) { mirror.FeedByCategory(); mirror.FeedByCallback(); }

        var t0 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < Frames; f++) mirror.FeedByCategory();
        var byCategory = GC.GetAllocatedBytesForCurrentThread() - t0;

        var t1 = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < Frames; f++) mirror.FeedByCallback();
        var byCallback = GC.GetAllocatedBytesForCurrentThread() - t1;

        Check.WithCustomMessage(
                $"the category form allocated {byCategory} bytes over {Frames} frames. Passing an"
                + " enum by value must cost nothing; if this is non-zero the mirror has drifted"
                + " from the shape it is supposed to mirror.")
            .That(byCategory).IsEqualTo(0L);

        Check.WithCustomMessage(
                $"the callback form allocated {byCallback} bytes, not more than zero — a"
                + " this-capturing lambda argument no longer allocates per call on this runtime,"
                + " so the stated reason UpdateButtonPositions takes a DwellCategory no longer"
                + " holds. Re-verify the remarks on UpdateButtonPositions before trusting them;"
                + " do not just delete this assertion.")
            .That(byCallback).IsStrictlyGreaterThan(0L);
    }

    private static string ReadSettingsWindow()
        => File.ReadAllText(Path.Combine(
            RepoRoot().FullName, "src/EyeCtrl.App/Views/SettingsWindow.xaml.cs"));

    private static DirectoryInfo RepoRoot()
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(
            typeof(SettingsFrameLoopAllocationTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            dir = dir.Parent;

        Check.WithCustomMessage("Could not locate the repo root (EyeCtrl.slnx)")
            .That(dir).IsNotNull();
        return dir!;
    }

    /// <summary>
    /// <c>UpdateAllTargetPositions</c> reduced to the part that allocated: four helper calls per
    /// frame, differing only in how the category arrives. The <c>Feed</c> overloads keep the real
    /// method's <c>try</c>/<c>catch</c> because the JIT does not inline a method containing an
    /// exception handler — the real one is therefore never inlined, its delegate argument always
    /// escapes, and a mirror without the handler could be inlined into a shape where the delegate
    /// does not escape at all. That would measure something the production code never does.
    /// </summary>
    private sealed class FrameLoopMirror
    {
        // Empty on purpose: the delegate is created at the call site whether or not there is a
        // border to feed, so an empty map isolates the argument's cost from the loop's work.
        private readonly Dictionary<string, string> _borders = new();
        private int _sink;

        public void FeedByCategory()
        {
            Feed(_borders, DwellCategory.Tab);
            Feed(_borders, DwellCategory.Tab);
            Feed(_borders, DwellCategory.Pagination);
            Feed(_borders, DwellCategory.Modal);
        }

        public void FeedByCallback()
        {
            // Captures `this` via Record, exactly like the lambdas that read _viewModel did.
            Feed(_borders, (id, v) => Record(id, DwellCategory.Tab, v));
            Feed(_borders, (id, v) => Record(id, DwellCategory.Tab, v));
            Feed(_borders, (id, v) => Record(id, DwellCategory.Pagination, v));
            Feed(_borders, (id, v) => Record(id, DwellCategory.Modal, v));
        }

        private void Feed(Dictionary<string, string> borders, DwellCategory category)
        {
            foreach (var (id, value) in borders)
            {
                try
                {
                    Record(id, category, value);
                }
                catch (InvalidOperationException)
                {
                    // Mirrors the real method's skip-if-not-rendered branch.
                }
            }
        }

        private void Feed(Dictionary<string, string> borders, Action<string, string> updateAction)
        {
            foreach (var (id, value) in borders)
            {
                try
                {
                    updateAction(id, value);
                }
                catch (InvalidOperationException)
                {
                    // Mirrors the real method's skip-if-not-rendered branch.
                }
            }
        }

        private void Record(string id, DwellCategory category, string value)
            => _sink += id.Length + (int)category + value.Length;
    }
}
