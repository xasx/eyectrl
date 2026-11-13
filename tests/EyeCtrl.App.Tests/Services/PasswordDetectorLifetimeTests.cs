namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Linq;

/// <summary>
/// The sensitive-context detector (the system-wide UIA focus hook) is the keyboard's E1 password mute,
/// so it runs for the whole session, NOT with the adaptation runtime. It used to start only in
/// <c>AdaptationActivation.StartRuntime</c>: with adaptation off (the default, and the owner's setting)
/// it never ran, and the owner's logs of 05.–12.11 hold 0 <c>KB-E1 sensitive</c> markers against 195
/// <c>KB-E1 key</c> lines (owner decision 12.11). <c>App.OnStartup</c>/<c>OnExit</c> cannot run headless and
/// a real start would register a real system-wide hook, so the lifetime is pinned at source; the
/// keyboard's receipt of the detector is pinned against the real container in
/// <see cref="KeyboardE1WiringTests"/>.
/// </summary>
public class PasswordDetectorLifetimeTests
{
    private const string StartCall = "_serviceProvider.GetRequiredService<ISensitiveContextDetector>().Start();";

    private static string Source(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App" }.Concat(parts).ToArray()));

    /// <summary>The body of the first method whose declaration starts with <paramref name="signature"/>, braces matched.</summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Check.WithCustomMessage($"signature not found: {signature}").That(start).IsStrictlyGreaterThan(-1);
        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException($"unbalanced body: {signature}");
    }

    [Fact]
    public void OnStartup_StartsTheDetector_Unconditionally()
    {
        var body = MethodBody(Source("App.xaml.cs"), "protected override void OnStartup(");
        var lines = body.Split('\n').Select(l => l.Trim()).ToList();

        var at = lines.FindIndex(l => l == StartCall);
        Check.WithCustomMessage("OnStartup must start the sensitive-context detector as its own statement "
                + "(the keyboard's E1 password mute; it must not wait for adaptation)")
            .That(at).IsStrictlyGreaterThan(-1);

        // The previous code line (comments and blanks skipped) must not guard the call.
        var previous = lines.Take(at).LastOrDefault(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)) ?? string.Empty;
        Check.WithCustomMessage($"the detector start must not be conditional; the line before it is: {previous}")
            .That(previous.StartsWith("if", StringComparison.Ordinal) || previous.StartsWith("else", StringComparison.Ordinal))
            .IsFalse();
        Check.WithCustomMessage("exactly one startup start call")
            .That(lines.Count(l => l.Contains("ISensitiveContextDetector>().Start()", StringComparison.Ordinal)))
            .IsEqualTo(1);

        // Review r1 F1: the previous-line check misses a BRACED if and a move into a lambda/event handler.
        // The call must sit at the same block depth as the adaptation runtime's own startup line, which
        // runs on every startup path.
        var anchor = lines.FindIndex(l => l == "_serviceProvider.GetRequiredService<AdaptationActivation>().Start(gazeEngine);");
        Check.WithCustomMessage("the adaptation startup anchor line must exist in OnStartup")
            .That(anchor).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the detector start must be at the same block depth as AdaptationActivation.Start "
                + "(not inside an if-block, a lambda or an event handler)")
            .That(DepthAt(lines, at)).IsEqualTo(DepthAt(lines, anchor));
    }

    /// <summary>Brace depth before line <paramref name="index"/>, ignoring string/char literals and // comments.</summary>
    private static int DepthAt(System.Collections.Generic.IReadOnlyList<string> lines, int index)
    {
        var depth = 0;
        for (var i = 0; i < index; i++)
        {
            var code = System.Text.RegularExpressions.Regex.Replace(lines[i], "\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])'", string.Empty);
            var comment = code.IndexOf("//", StringComparison.Ordinal);
            if (comment >= 0) code = code.Substring(0, comment);
            depth += code.Count(c => c == '{') - code.Count(c => c == '}');
        }
        return depth;
    }

    [Fact]
    public void TheAdaptationRuntime_NeitherStartsNorStopsTheDetector()
    {
        var source = Source("Services", "AdaptationActivation.cs");
        var start = MethodBody(source, "private static void StartRuntime(");
        var stop = MethodBody(source, "private static void StopRuntime(");

        Check.WithCustomMessage("StartRuntime must not own the detector (it runs for the whole session)")
            .That(start.Contains("GetRequiredService<ISensitiveContextDetector>()", StringComparison.Ordinal)).IsFalse();
        Check.WithCustomMessage("StopRuntime must not stop the detector: switching adaptation off would silently "
                + "re-open every password field to per-key E1 lines")
            .That(stop.Contains("GetRequiredService<ISensitiveContextDetector>()", StringComparison.Ordinal)).IsFalse();
    }

    [Fact]
    public void OnExit_StopsTheDetector_BeforeTheProviderIsDisposed()
    {
        var body = MethodBody(Source("App.xaml.cs"), "protected override void OnExit(");

        var stop = body.IndexOf(
            "Step(\"stop password-field detector\", () => _serviceProvider.GetRequiredService<ISensitiveContextDetector>().Stop());",
            StringComparison.Ordinal);
        var dispose = body.IndexOf("Step(\"dispose service provider\"", StringComparison.Ordinal);

        Check.WithCustomMessage("OnExit must remove the system-wide focus hook in its own step")
            .That(stop).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the stop must run while the provider is still alive")
            .That(stop).IsStrictlyLessThan(dispose);
    }
}
