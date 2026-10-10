namespace EyeCtrl.Tests.TestSupport;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using EyeCtrl.Core.Localization;

/// <summary>
/// T09's fit harness, shared by every project that measures translated text in its real geometry
/// (EyeCtrl.App.Tests and EyeCtrl.Supervisor.Tests link this one file — one rule, one copy).
///
/// <para>
/// <b>The host.</b> <see cref="FitHost"/> lays a production element out inside an off-screen
/// <c>WS_POPUP</c> <see cref="HwndSource"/> that is never made visible — no <c>Window.Show()</c>,
/// nothing on a gaze-only user's screen, and runnable in CI's <c>Category!=UI</c> set. The root
/// carries a <see cref="ScaleTransform"/> of <c>2.0 / device scale</c>, so the effective scale is
/// the user's 200 % on any machine: 2.0 on a 96-DPI CI runner and 1.0 here. <c>DpiHelper</c> is
/// never written (that would be vacuous for layout and would need the exclusive collection).
/// </para>
///
/// <para>
/// <b>Three guards, each of which throws rather than letting a measurement pass for nothing.</b>
/// A missing <see cref="PresentationSource"/>; an effective scale that is not 2.0 (read back
/// through <c>PointToScreen</c>, which composes the whole transform chain); and any element in
/// the hosted tree at <see cref="TextFormattingMode.Display"/>, where glyph advances snap to the
/// pixel grid and a DIP-space measurement no longer describes what is drawn.
/// </para>
///
/// <para>
/// <b>Wrapping is measured through the production <see cref="TextBlock"/>.</b>
/// <see cref="FitText.NaturalHeight"/> re-measures the real, arranged element at its real width
/// with unbounded height, then restores the layout. Never <see cref="FormattedText"/> with
/// <c>MaxTextWidth</c>, which ellipsizes by default. Unbreakable runs are measured with a probe
/// <see cref="TextBlock"/> that copies the production element's font properties
/// (<see cref="FitText.NaturalWidth"/>): a probe, so a bound <c>Text</c> is never overwritten.
/// </para>
/// </summary>
internal sealed class FitHost : IDisposable
{
    /// <summary>The user's display: 3840×2400 at 200 %.</summary>
    internal const double Scale = 2.0;

    private const int WsPopup = unchecked((int)0x80000000);

    private FitHost(HwndSource source, Border frame, FrameworkElement content)
    {
        Source = source;
        Frame = frame;
        Content = content;
    }

    /// <summary>The invisible, off-screen source.</summary>
    internal HwndSource Source { get; }

    /// <summary>The root: carries the scale transform and any inherited window font.</summary>
    internal Border Frame { get; }

    /// <summary>The production element being measured.</summary>
    internal FrameworkElement Content { get; }

    /// <summary>
    /// Hosts <paramref name="content"/> at effective scale 2.0 and lays it out.
    /// </summary>
    /// <param name="content">A production element, detached from any window.</param>
    /// <param name="width">The frame's width in DIP (a window's client width), or null for unbounded.</param>
    /// <param name="height">The frame's height in DIP, or null for unbounded.</param>
    /// <param name="inheritFrom">
    /// The window the content was detached from. Its font family, size, weight, style and stretch
    /// are inheritable, and <c>OverlayWindow</c> sets the family on the window itself — a detached
    /// subtree would otherwise measure in the WPF default face.
    /// </param>
    /// <param name="family">
    /// The font family to inherit when there is no window to copy it from — a Settings control is
    /// hosted by <c>SettingsWindow</c>, an <c>OverlayWindow</c> that sets the theme family.
    /// </param>
    /// <returns>The laid-out host. Dispose it.</returns>
    internal static FitHost Open(
        FrameworkElement content, double? width = null, double? height = null, Control? inheritFrom = null,
        FontFamily? family = null)
    {
        var source = new HwndSource(new HwndSourceParameters("t09-fit-probe")
        {
            WindowStyle = WsPopup,
            PositionX = -30000,
            PositionY = -30000,
            Width = 1,
            Height = 1,
        });

        var device = source.CompositionTarget!.TransformToDevice.M11;
        var factor = Scale / device;
        var frame = new Border
        {
            Child = content,
            LayoutTransform = new ScaleTransform(factor, factor),
        };
        if (width is { } w) frame.Width = w;
        if (height is { } h) frame.Height = h;
        if (inheritFrom is not null)
        {
            frame.SetValue(TextElement.FontFamilyProperty, inheritFrom.FontFamily);
            frame.SetValue(TextElement.FontSizeProperty, inheritFrom.FontSize);
            frame.SetValue(TextElement.FontWeightProperty, inheritFrom.FontWeight);
            frame.SetValue(TextElement.FontStyleProperty, inheritFrom.FontStyle);
            frame.SetValue(TextElement.FontStretchProperty, inheritFrom.FontStretch);
            TextOptions.SetTextFormattingMode(frame, TextOptions.GetTextFormattingMode(inheritFrom));
        }

        if (family is not null) frame.SetValue(TextElement.FontFamilyProperty, family);

        source.RootVisual = frame;
        var host = new FitHost(source, frame, content);
        host.Relayout();
        host.Guard();
        return host;
    }

    /// <summary>Re-runs layout over the whole frame (after a text change or a probe measure).</summary>
    internal void Relayout()
    {
        Frame.InvalidateMeasure();
        Frame.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Frame.Arrange(new Rect(Frame.DesiredSize));
        Frame.UpdateLayout();
    }

    /// <summary>The three guards. Called by <see cref="Open"/>; callers may re-run it after edits.</summary>
    internal void Guard()
    {
        if (PresentationSource.FromVisual(Content) is null)
            throw new InvalidOperationException(
                "FitHost: no PresentationSource. Every measurement below would describe an "
                + "unhosted element at the machine's own DPI, not the user's 200 %. Fix the "
                + "fixture; never delete this guard.");

        var origin = Content.PointToScreen(new Point(0, 0));
        var hundred = Content.PointToScreen(new Point(100, 0));
        var effective = (hundred.X - origin.X) / 100.0;
        if (Math.Abs(effective - Scale) > 1e-6)
            throw new InvalidOperationException(
                $"FitHost: the effective scale is {effective.ToString("0.###", CultureInfo.InvariantCulture)}, "
                + $"not {Scale}. The measurement would not be at the user's 200 %.");

        var display = DisplayModeElements(Frame).ToList();
        if (display.Count > 0)
            throw new InvalidOperationException(
                "FitHost: TextFormattingMode=Display on " + string.Join(", ", display)
                + ". Display mode snaps glyph advances to the device pixel grid, so a DIP-space "
                + "fit measurement does not describe what is drawn. The harness refuses it.");
    }

    /// <summary>Every element in the visual tree whose effective formatting mode is Display.</summary>
    internal static IEnumerable<string> DisplayModeElements(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (TextOptions.GetTextFormattingMode(node) == TextFormattingMode.Display)
            {
                var name = node is FrameworkElement { Name.Length: > 0 } fe ? fe.Name : "";
                yield return $"{node.GetType().Name}{(name.Length > 0 ? " '" + name + "'" : "")}";
            }

            if (node is not Visual && node is not System.Windows.Media.Media3D.Visual3D) continue;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Source.RootVisual = null;
        Source.Dispose();
        // Release the content so the same production tree can be hosted again (a measurer that
        // first finds a natural size, then lays the tree out at the size the window would take).
        Frame.Child = null;
    }
}

/// <summary>One class verdict for one text: the pass/fail and the numbers behind it.</summary>
/// <param name="Fits">True when the text meets every rule of the class.</param>
/// <param name="Detail">The measured numbers and, on failure, which rule broke.</param>
/// <param name="Face">The resolved font face the measurement used.</param>
/// <param name="Margin">
/// The verdict's tightest slack in DIP — room less need, so positive while the text fits and
/// negative by how much it does not — or NaN where a class has no single figure. A recorded English
/// exception is bounded by it: the measurement may not get worse than the figure on record.
/// </param>
/// <param name="Unwaivable">
/// A rule the text broke that <paramref name="Margin"/> does not measure, or null. An exception
/// bounds the margin's rule only, so a key with a recorded exception still fails on this one
/// (T09 review-03, F2: the ring margin must not waive the Settings preview's labels).
/// </param>
internal sealed record FitVerdict(bool Fits, string Detail, string Face, double Margin = double.NaN, string? Unwaivable = null)
{
    public override string ToString() => $"{(Fits ? "fits" : "FAILS")}: {Detail} [{Face}]";
}

/// <summary>The text arithmetic every class shares.</summary>
internal static class FitText
{
    /// <summary>Formats a DIP figure for messages, culture-invariant.</summary>
    internal static string Dip(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The unbreakable runs the class-B rule measures: the text split at spaces, after hyphens
    /// (the hyphen stays on its line, so it is part of the run before it) and at U+00AD, where a
    /// break draws a hyphen (so it is measured with one). Newlines split too.
    /// </summary>
    internal static IReadOnlyList<string> Runs(string text)
    {
        var runs = new List<string>();
        var current = new System.Text.StringBuilder();

        void Flush()
        {
            if (current.Length > 0) runs.Add(current.ToString());
            current.Clear();
        }

        foreach (var c in text)
        {
            switch (c)
            {
                case ' ':
                case '\n':
                case '\r':
                case '\t':
                    Flush();
                    break;
                case '-':
                case '‐':
                    current.Append(c);
                    Flush();
                    break;
                case '­':
                    current.Append('-');
                    Flush();
                    break;
                default:
                    current.Append(c);
                    break;
            }
        }

        Flush();
        return runs;
    }

    /// <summary>
    /// The one-line width of <paramref name="text"/> in <paramref name="like"/>'s font, measured by
    /// a probe <see cref="TextBlock"/> with no wrap and no constraint.
    /// </summary>
    internal static double NaturalWidth(TextBlock like, string text)
    {
        var probe = new TextBlock
        {
            Text = text,
            FontFamily = like.FontFamily,
            FontSize = like.FontSize,
            FontWeight = like.FontWeight,
            FontStyle = like.FontStyle,
            FontStretch = like.FontStretch,
            TextWrapping = TextWrapping.NoWrap,
        };
        TextOptions.SetTextFormattingMode(probe, TextOptions.GetTextFormattingMode(like));
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        // Rounded UP to the device pixel at the user's 2.0 (review-01 N4): the probe is unhosted
        // and unrounded, while a production root with UseLayoutRounding snaps widths to the pixel
        // grid, so the raw figure could read up to half a DIP generous. Rounding up keeps the probe
        // never more lenient than the drawn text.
        return Math.Ceiling(probe.DesiredSize.Width * FitHost.Scale) / FitHost.Scale;
    }

    /// <summary>The widest unbreakable run of <paramref name="text"/> in <paramref name="like"/>'s font.</summary>
    internal static (string Run, double Width) WidestRun(TextBlock like, string text) =>
        Runs(text)
            .Select(run => (run, NaturalWidth(like, run)))
            .DefaultIfEmpty(("", 0.0))
            .MaxBy(pair => pair.Item2);

    /// <summary>
    /// The full laid-out height of the PRODUCTION element's own box when that box is
    /// <paramref name="width"/> wide — its real wrapping, with no height constraint (an arranged
    /// element's <c>DesiredSize</c> is clamped to its slot and would hide an overflow). Margins are
    /// outside the box on both axes: <c>Measure</c> counts them inside the available size, so they
    /// are added to the width offered and taken off the height returned. The host's layout is
    /// restored afterwards.
    /// </summary>
    internal static double NaturalHeight(FitHost host, FrameworkElement element, double width)
    {
        var margin = element.Margin;
        element.Measure(new Size(width + margin.Left + margin.Right, double.PositiveInfinity));
        var height = element.DesiredSize.Height - margin.Top - margin.Bottom;
        element.InvalidateMeasure();
        host.Relayout();
        return height;
    }

    /// <summary>
    /// The face a <see cref="TextBlock"/>'s family chain resolves to on THIS machine — logged by
    /// every class, because a CI runner without Segoe UI Variable measures in Segoe UI.
    /// </summary>
    internal static string ResolvedFace(TextBlock text) =>
        ResolvedFace(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch, text.FontSize);

    /// <summary>The resolved face for a set of font properties.</summary>
    internal static string ResolvedFace(
        FontFamily family, FontStyle style, FontWeight weight, FontStretch stretch, double size)
    {
        var typeface = new Typeface(family, style, weight, stretch);
        var file = typeface.TryGetGlyphTypeface(out var glyphs)
            ? Path.GetFileName(glyphs.FontUri.LocalPath)
            : "<unresolved>";
        return $"{family.Source} → {file} {weight} {Dip(size)} DIP";
    }

    /// <summary>The resolved font FILE only — the identity an exception's staleness check keys on.</summary>
    internal static string ResolvedFile(TextBlock text)
    {
        var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        return typeface.TryGetGlyphTypeface(out var glyphs)
            ? Path.GetFileName(glyphs.FontUri.LocalPath)
            : "<unresolved>";
    }

    /// <summary>All TextBlocks under <paramref name="root"/> in the visual tree.</summary>
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is T match) yield return match;
            if (node is not Visual) continue;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}

/// <summary>
/// The English table's keys by fit class, read from the embedded <c>strings.json</c> — the same
/// bytes production loads — with plural forms expanded.
/// </summary>
internal static class FitCatalog
{
    /// <summary>One English text to measure: its key, plural form ("" when singular) and text.</summary>
    internal sealed record Item(string Key, string Form, string Text, IReadOnlyList<string> Args)
    {
        public override string ToString() => Form.Length == 0 ? Key : $"{Key} [{Form}]";
    }

    /// <summary>Every English entry's key → fit class.</summary>
    internal static IReadOnlyDictionary<string, string> FitByKey()
    {
        using var document = Document();
        return document.RootElement.EnumerateObject()
            .Where(p => p.Name != "_meta")
            .ToDictionary(p => p.Name, p => p.Value.GetProperty("fit").GetString()!, StringComparer.Ordinal);
    }

    /// <summary>The English items of one class, sorted by key then form.</summary>
    internal static IReadOnlyList<Item> ItemsOf(string fitClass)
    {
        using var document = Document();
        var items = new List<Item>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Name == "_meta") continue;
            if (entry.Value.GetProperty("fit").GetString() != fitClass) continue;

            var args = entry.Value.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList();
            var text = entry.Value.GetProperty("text");
            if (text.ValueKind == JsonValueKind.String)
            {
                items.Add(new Item(entry.Name, "", text.GetString()!, args));
                continue;
            }

            foreach (var form in text.EnumerateObject())
                items.Add(new Item(entry.Name, form.Name, form.Value.GetString()!, args));
        }

        return items.OrderBy(i => i.Key, StringComparer.Ordinal).ThenBy(i => i.Form, StringComparer.Ordinal).ToList();
    }

    private static JsonDocument Document()
    {
        using var stream = typeof(StringTable).Assembly.GetManifestResourceStream(StringTable.EmbeddedResourceName)
            ?? throw new InvalidOperationException($"The embedded resource '{StringTable.EmbeddedResourceName}' is missing.");
        return JsonDocument.Parse(stream);
    }
}

/// <summary>One row of a non-binding fit report.</summary>
internal sealed record FitReportRow(string Theme, string FitClass, string Key, string Text, FitVerdict Verdict);

/// <summary>
/// T09 PR 2's NON-BINDING fit report: the fit classes measured under the pseudo-locale, written as
/// a Markdown file into the test output directory (<c>t09-fit-report/</c>) as an early warning for
/// T10. It never fails a build; the test that writes it asserts only that it covered every key.
/// </summary>
internal static class FitReport
{
    /// <summary>The report directory under the test output.</summary>
    internal static string Directory => Path.Combine(AppContext.BaseDirectory, "t09-fit-report");

    /// <summary>Writes <paramref name="rows"/> as a Markdown report and returns its path.</summary>
    internal static string Write(string fileName, string title, IReadOnlyList<FitReportRow> rows, IEnumerable<string>? notes = null)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, fileName);
        var md = new System.Text.StringBuilder();
        md.AppendLine("# " + title).AppendLine();
        md.AppendLine("Non-binding: the pseudo-locale (every word +40 %, accented, bracketed) measured in each class's ")
          .AppendLine("production geometry at scale 2.0. An entry that does not fit is an early warning for T10's German.")
          .AppendLine();
        foreach (var group in rows.GroupBy(r => (r.Theme, r.FitClass)))
        {
            var failing = group.Count(r => !r.Verdict.Fits);
            md.AppendLine($"- **{group.Key.FitClass}** ({group.Key.Theme}): {group.Count() - failing} fit, {failing} do not");
        }

        md.AppendLine().AppendLine("| Theme | Class | Key | Pseudo text | Verdict |").AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var row in rows.OrderBy(r => r.Verdict.Fits).ThenBy(r => r.Theme, StringComparer.Ordinal)
                     .ThenBy(r => r.FitClass, StringComparer.Ordinal).ThenBy(r => r.Key, StringComparer.Ordinal))
        {
            md.AppendLine($"| {row.Theme} | {row.FitClass} | `{row.Key}` | {Cell(row.Text)} | "
                          + $"{(row.Verdict.Fits ? "fits" : "**does not fit**")}: {Cell(row.Verdict.Detail)} |");
        }

        if (notes is not null)
        {
            md.AppendLine().AppendLine("## Notes").AppendLine();
            foreach (var note in notes) md.AppendLine("- " + Cell(note));
        }

        File.WriteAllText(path, md.ToString(), new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string Cell(string text) =>
        text.Replace("|", @"\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
