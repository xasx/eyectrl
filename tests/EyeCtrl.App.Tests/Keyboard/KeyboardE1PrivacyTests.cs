namespace EyeCtrl.App.Tests.Keyboard;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Overlays;
using EyeCtrl.App.Tests.Fixtures;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Microsoft.Extensions.Logging;
using NFluent;
using Xunit;

/// <summary>
/// THE PRIVACY GATE for E1, the keyboard's per-key log. The owner approved the logging on one
/// condition: <b>no characters, no words, no window titles, enforced by a test</b>. This is that
/// test, and it is a merge gate.
///
/// <para>
/// A real <see cref="KeyboardWindow"/> and <see cref="KeyboardViewModel"/> run a headless typing
/// session through the real gaze chain (<c>UpdateGaze</c> → <c>ProcessKeyGaze</c> → the window's
/// activation handler): letters, a bar pick, a Space, Backspace, a mode switch, Enter and a hide,
/// on the shipped QWERTZ layout, with a distinctive marker word. Every E1 line it produces is
/// captured and checked three ways:
/// </para>
/// <list type="number">
/// <item>the marker, and every letter of it as a key label, appears in no rendered line and no
/// property value;</item>
/// <item>the property NAMES of each event equal an allow-list written out in this file, so a new
/// field needs a deliberate edit here (and so a review of whether it can carry content);</item>
/// <item>every STRING property value is from a closed vocabulary — a class, a layout/action id, an
/// edge, a source, a cause. A free-text value cannot pass, whatever its name.</item>
/// </list>
/// <para>
/// The probe the session runs against even returns the typed text as the "focused control", to
/// prove it never reaches a line, and <see cref="IKeyboardContextProbe"/> has no member that could
/// return a window title (a test below pins that). Mutants (each must turn this class RED): log
/// the key's character as the action id; add a <c>{Char}</c> property; log the picked word.
/// </para>
/// </summary>
[Collection("WpfApplication")]
public class KeyboardE1PrivacyTests
{
    private const int DwellMs = 500;
    private const long DwellUs = DwellMs * 1000L;
    private const int WsPopup = unchecked((int)0x80000000);
    private const string Marker = "Zebrafink";
    private const string ProcessName = "WINWORD.exe";

    // ---- the allow-lists --------------------------------------------------------------------

    /// <summary>Key classes whose lines carry NO position or id: text keys (they would spell the text).</summary>
    private static readonly HashSet<string> TextKeyClasses = ["letter", "digit", "symbol", "emoji"];

    /// <summary>The names that identify a key or fingerprint its position. A text key's line must carry NONE.</summary>
    private static readonly string[] IdentityNames = ["Row", "Col", "Act"];

    /// <summary>
    /// Everything a text key's key/abandon line must NOT carry: the key's identity (position, id), its box
    /// size and pixel offsets (the tracker's bias field and fractional display scales turn those into a
    /// column), and the arrival/likelihood facts that narrow a word. Those exist only for non-text keys,
    /// and for a CORRECTED letter on the corr and slip lines.
    /// </summary>
    private static readonly string[] TextKeyBannedNames =
        ["Row", "Col", "Act", "BoxW", "BoxH", "Dx", "Dy", "MeanDx", "MeanDy", "Saccade", "Restarts", "Likely",
         "Start", "NoCont"];

    private static readonly Dictionary<string, string[]> AllowedNames = new()
    {
        // The names of a TEXT key's line. A non-text key (Space, Backspace, Enter, modifiers, switches,
        // shortcuts) instead carries NonTextKeyNames. See NamesAllowedFor. No edge-quarter class on any
        // text line: the quarter is only COUNTED, per dock edge, on the "edgeq" tally line.
        ["key"] = ["Seq", "TsUs", "Class", "Sub", "Edge", "WinX", "WinY", "WinW", "WinH", "SinceMoveMs",
                   "SinceKeyMs", "DwellMs", "Penalty", "Typing", "Bs"],
        ["edgeq"] = ["Edge", "Fires", "C", "N", "NE", "E", "SE", "S", "SW", "W", "NW",
                     "Aborts", "AC", "AN", "ANE", "AE", "ASE", "AS", "ASW", "AW", "ANW"],
        ["holdon"] = ["Seq", "Ms", "Capped"],
        ["abandon"] = ["TsUs", "Class", "Pct", "Why", "Sub", "Edge"],
        ["slip"] = ["Adj", "Dir", "Dist", "Ms", "Reg", "Saccade", "Restarts", "Likely", "Start", "NoCont"],
        ["corr"] = ["Seq", "Saccade", "Restarts", "Likely", "Start", "NoCont"],
        ["pick"] = ["Slot", "Source", "PrefixLen", "InsLen", "SinceKeyMs", "SincePickMs", "RenderMs", "Visited"],
        ["pickundo"] = ["Ms", "By"],
        ["space"] = ["Seq", "Empty"],
        ["mode"] = ["Kind", "From", "To"],
        ["modeundo"] = ["Kind", "Ms"],
        ["edge"] = ["Button", "Edge"],
        ["shown"] = ["Proc", "Edge", "Via", "BoxW", "BoxH"],
        ["layout"] = ["Sub", "BoxW", "BoxH"],
        ["sensitive"] = [],
        ["hidden"] = ["Cause", "VisibleMs"],
        ["mirror"] = ["At", "Res", "MirrorLen", "CtlLen", "Common"],
    };

    private static readonly string[] NonTextKeyNames =
        ["Row", "Col", "Act", "BoxW", "BoxH", "Dx", "Dy", "MeanDx", "MeanDy", "Restarts", "Saccade"];
    private static readonly string[] NonTextAbandonNames = ["Row", "Col", "Act", "MeanDx", "MeanDy"];

    /// <summary>The closed vocabulary of every string-valued property, by property name.</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedStrings = new()
    {
        ["Class"] = ["letter", "digit", "symbol", "space", "backspace", "deleteword", "enter", "shift", "caps", "mode",
                     "switchsub", "emoji", "other"],
        ["Saccade"] = ["short", "mid", "long", "-"],
        ["Dir"] = ["N", "NE", "E", "SE", "S", "SW", "W", "NW", "-"],
        ["Reg"] = ["top", "mid", "bot", "-"],
        ["Act"] = ["space", "backspace", "deleteWord", "enter", "shift", "modifier:shift", "toggle:capslock",
                   "modifier:ctrl", "modifier:alt", "modifier:win", "switchEmoji", "switchSub:letters",
                   "switchSub:numbers", "switchSub:symbols", "switchSub:accents", "switchSub:function",
                   "tab", "addWord"],
        ["Sub"] = ["letters", "numbers", "symbols", "accents", "function"],
        ["Edge"] = ["top", "bottom", "-"],
        ["Bs"] = ["-", "word", "committed", "none"],
        ["Why"] = ["NearestChanged", "GazeLeft"],
        ["Source"] = ["dict", "fuzzy", "pair", "phrase", "sentence", "lm", "other"],
        ["By"] = ["backspace", "deleteword"],
        ["Kind"] = ["sublayout", "emoji", "shift", "caps", "ctrl", "alt", "win", "flip"],
        ["From"] = ["letters", "numbers", "symbols", "accents", "function", "text", "emoji", "on", "off", "top", "bottom"],
        ["To"] = ["letters", "numbers", "symbols", "accents", "function", "text", "emoji", "on", "off", "top", "bottom"],
        ["Button"] = ["flip", "close"],
        ["Proc"] = [ProcessName, "unknown"],
        ["Via"] = ["toolbar-toggle", "ring", "request", "probe"],
        ["Cause"] = ["toolbar-toggle", "edge-close", "request", "probe"],
        ["At"] = ["hide", "enter"],
        ["Res"] = ["match", "mismatch", "unsupported", "suppressed", "empty"],
    };

    /// <summary>The allowed property names of one logged line: by event, and for key/abandon lines by class.</summary>
    private static string[] NamesAllowedFor(Line line)
    {
        var names = AllowedNames[line.Event];
        if (line.Event is "key" or "abandon" && !TextKeyClasses.Contains((string)line.Props["Class"]!))
            return names.Concat(line.Event == "key" ? NonTextKeyNames : NonTextAbandonNames).ToArray();
        return names;
    }

    // ---- capture ----------------------------------------------------------------------------

    private sealed record Line(string Event, string Message, IReadOnlyDictionary<string, object?> Props);

    private sealed class Capture : ILogger<KeyboardViewModel>
    {
        private readonly object _gate = new();
        private readonly List<Line> _lines = new();

        /// <summary>Makes <see cref="IsEnabled"/> throw, as a broken logging stack would.</summary>
        public volatile bool ThrowOnIsEnabled;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) =>
            ThrowOnIsEnabled ? throw new InvalidOperationException("logger broke") : true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.StartsWith("KB-E1 ", StringComparison.Ordinal)) return;
            var props = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> list)
                foreach (var (k, v) in list)
                    if (k != "{OriginalFormat}") props[k] = v;
            lock (_gate)
                _lines.Add(new Line(message.Split(' ')[1], message, props));
        }

        public List<Line> All() { lock (_gate) return _lines.ToList(); }
    }

    // ---- the session ------------------------------------------------------------------------

    private sealed class SpyProbe : IKeyboardContextProbe
    {
        public string? GetForegroundProcessName() => ProcessName;
        public string? ReadFocusedTextTail(int maxChars) => "Hallo " + Marker.ToLowerInvariant();
    }

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words;
        public InMemoryDictProvider(Dictionary<string, int> words) => _words = words;
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    /// <summary>The recorder's clock under test: set from each gaze sample's timestamp.</summary>
    private sealed class FakeClock { public long Ms; }

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source, Capture Log,
        KeyboardLayout Layout, FakeClock Clock) : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Dictionary<string, PhysicalRect> Boxes(KeyboardWindow w) =>
        (Dictionary<string, PhysicalRect>)typeof(KeyboardWindow)
            .GetField("_keyScreenBoxes", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static readonly Dictionary<string, int> SessionWords = new()
    {
        ["zebrafink"] = 900, ["zebra"] = 500, ["hallo"] = 700, ["welt"] = 300,
    };

    private static Probe Open(Dictionary<string, int>? words = null, double scale = 1.0, double keyMultiplier = 1.0,
        ISensitiveContextDetector? sensitive = null)
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var log = new Capture();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger(),
            logger: log, contextProbe: new SpyProbe(), sensitiveContext: sensitive);
        var clock = new FakeClock();
        vm.UseE1Recorder(new KeyboardE1Recorder(log, () => clock.Ms));
        vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(
            new InMemoryDictProvider(words ?? SessionWords), "QWERTZ")));

        var layout = KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(layout, keyMultiplier, keyDwellMs: DwellMs, predictionDwellMs: 800, cooldownMs: 0);
        w.SetSettings(settings);

        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var source = new HwndSource(new HwndSourceParameters("kb-e1-privacy-probe")
        {
            WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
        });
        var factor = scale / source.CompositionTarget!.TransformToDevice.M11;
        content.LayoutTransform = new ScaleTransform(factor, factor);
        source.RootVisual = content;
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        content.Arrange(new Rect(content.DesiredSize));
        content.UpdateLayout();

        Check.WithCustomMessage(
                "No PresentationSource: the key-grid gate would read every sample as outside and the "
                + "whole session would type nothing, leaving this privacy gate passing over zero "
                + "lines. Fix the fixture, never delete this guard.")
            .That(PresentationSource.FromVisual(content)).IsNotNull();

        w.UpdateGaze(new PhysicalPoint(0, 0), 0);
        Check.That(Centres(w)).Not.IsEmpty();
        return new Probe(w, vm, content, source, log, layout, clock);
    }

    /// <summary>One gaze sample; the recorder's fake clock follows the sample's timestamp.</summary>
    private static void Gaze(Probe p, PhysicalPoint point, long timestampUs)
    {
        p.Clock.Ms = timestampUs / 1000;
        p.Window.UpdateGaze(point, timestampUs);
    }

    /// <summary>The key id (as the window mints it) whose output is <paramref name="output"/>.</summary>
    private static string IdOf(Probe p, string output)
    {
        var sub = p.Layout.SubLayouts[p.Vm.ActiveSubLayoutId];
        for (var r = 0; r < sub.Rows.Count; r++)
            for (var c = 0; c < sub.Rows[r].Count; c++)
                if (string.IsNullOrEmpty(sub.Rows[r][c].SpecialAction)
                    && string.Equals(sub.Rows[r][c].Output, output, StringComparison.OrdinalIgnoreCase))
                    return $"key_{r}_{c}";
        throw new InvalidOperationException($"no key outputs '{output}' on '{p.Vm.ActiveSubLayoutId}'");
    }

    private static string SpecialId(Probe p, string action)
    {
        var sub = p.Layout.SubLayouts[p.Vm.ActiveSubLayoutId];
        foreach (var row in sub.Rows)
            foreach (var key in row)
                if (string.Equals(key.SpecialAction, action, StringComparison.OrdinalIgnoreCase))
                    return key.SpecialAction!;
        throw new InvalidOperationException($"no '{action}' key on '{p.Vm.ActiveSubLayoutId}'");
    }

    private static void Dwell(Probe p, string keyId, ref long ts, long holdAfterUs = 0, long dwellUs = DwellUs)
    {
        p.Content.UpdateLayout();
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var centre = Centres(p.Window)[keyId];
        var latch = ts;
        Gaze(p, centre, latch); ts += 1_000;
        for (var t = latch + 50_000; t < latch + dwellUs; t += 50_000)
            Gaze(p, centre, t);
        Gaze(p, centre, latch + dwellUs);
        ts = latch + dwellUs + 1_000;
        if (holdAfterUs > 0)
        {
            for (var t = ts; t < ts + holdAfterUs; t += 50_000)
                Gaze(p, centre, t);
            ts += holdAfterUs;
        }
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
    }

    private static void RunTypingSession(Probe p)
    {
        long ts = 1_000;
        var vm = p.Vm;
        vm.IsVisible = true;
        vm.E1.RecordShown(vm.ForegroundProcessName(), "top", "ring");

        // "zeb" by gaze, then a bar pick of the marker word.
        Dwell(p, IdOf(p, "z"), ref ts, holdAfterUs: 300_000);
        Dwell(p, IdOf(p, "e"), ref ts);
        Dwell(p, IdOf(p, "b"), ref ts);
        p.Window.OnPredictionDwelled("pred_0");
        // Space after the pick; then a wrong letter and a Backspace inside the word.
        Dwell(p, SpecialId(p, "space"), ref ts);
        Dwell(p, IdOf(p, "k"), ref ts);
        Dwell(p, SpecialId(p, "backspace"), ref ts);
        Dwell(p, IdOf(p, "l"), ref ts);                 // the repair: the key beside k, so a slip line
        Dwell(p, SpecialId(p, "backspace"), ref ts);

        // An abandoned dwell: look at a key long enough to count, then away.
        var centres = Centres(p.Window);
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var start = ts;
        for (var t = start; t < start + 300_000; t += 50_000)
            Gaze(p, centres[IdOf(p, "f")], t);
        ts = start + 350_000;
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;

        // A mode round trip, a modifier, emoji enter/exit, Enter, a Flip and a hide.
        vm.SwitchSubLayout("numbers");
        vm.SwitchSubLayout("letters");
        vm.ToggleModifier(ModifierKey.Shift);
        vm.ToggleModifier(ModifierKey.Shift);
        vm.IsEmojiMode = true;
        vm.IsEmojiMode = false;
        vm.E1.RecordEdge("flip", "bottom", "top");
        Dwell(p, SpecialId(p, "enter"), ref ts, dwellUs: 700_000);   // an app-escaping key: its floor is 600 ms
        vm.E1.RecordEdge("close", "bottom", "bottom");
        vm.BeginMirrorCheck("hide");
        vm.E1.RecordHidden("edge-close");
    }

    private static List<Line> CaptureSession()
    {
        var p = Open();
        try
        {
            RunTypingSession(p);
            // The mirror verdicts are logged off-thread: wait for both.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (p.Log.All().Count(l => l.Event == "mirror") < 2 && DateTime.UtcNow < deadline)
                Thread.Sleep(20);
            return p.Log.All();
        }
        finally { p.Dispose(); }
    }

    // ---- the gates --------------------------------------------------------------------------

    [StaFact]
    public void TheSessionLogsEveryEventKind_SoTheGateBelowIsNotVacuous()
    {
        var lines = CaptureSession();
        var seen = lines.Select(l => l.Event).ToHashSet();

        foreach (var expected in new[]
                 {
                     "key", "holdon", "abandon", "pick", "space", "mode", "modeundo", "edge", "shown", "hidden",
                     "mirror", "pickundo", "slip", "corr", "layout", "edgeq",
                 })
        {
            Check.WithCustomMessage($"The typing session produced no '{expected}' line ({lines.Count} E1 lines in all).")
                .That(seen.Contains(expected)).IsTrue();
        }

        // z e b, Space, k, Backspace, l, Backspace, Enter.
        Check.That(lines.Count(l => l.Event == "key")).IsEqualTo(9);
    }

    [StaFact]
    public void NoLineAndNoPropertyValueCarriesTheMarkerWordTheControlTextOrAKeyLabel()
    {
        var lines = CaptureSession();
        var letters = Marker.ToLowerInvariant().Distinct().Select(c => c.ToString()).ToList();

        foreach (var line in lines)
        {
            var rendered = line.Message.ToLowerInvariant();
            Check.WithCustomMessage($"Marker word in: {line.Message}").That(rendered.Contains("zebrafink")).IsFalse();
            Check.WithCustomMessage($"The mirror's text in: {line.Message}").That(rendered.Contains("hallo")).IsFalse();

            foreach (var (name, value) in line.Props)
            {
                // A compass point (N, E, ...) is one letter by nature; its closed vocabulary is pinned
                // by EveryStringValue_IsFromAClosedVocabulary..., so it cannot carry a key label.
                if (name == "Dir") continue;
                var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                // Not the marker or any 3+ letter run of it, and never a single key label (a letter)
                // standing alone as a value — "act=z" is exactly what leaking a key's character looks like.
                Check.WithCustomMessage($"Property {name}='{text}' carries a letter-as-label in: {line.Message}")
                    .That(letters.Contains(text.ToLowerInvariant())).IsFalse();
                for (var i = 0; i + 3 <= Marker.Length; i++)
                    Check.WithCustomMessage($"Property {name}='{text}' carries a run of the marker in: {line.Message}")
                        .That(text.ToLowerInvariant().Contains(Marker.ToLowerInvariant().Substring(i, 3))).IsFalse();
            }
        }
    }

    [StaFact]
    public void EveryEventsPropertyNames_EqualTheAllowList()
    {
        var lines = CaptureSession();

        foreach (var line in lines)
        {
            Check.WithCustomMessage($"An E1 event with no allow-list entry: '{line.Event}' ({line.Message})")
                .That(AllowedNames.ContainsKey(line.Event)).IsTrue();
            var expected = NamesAllowedFor(line).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            var actual = line.Props.Keys.OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Check.WithCustomMessage(
                    $"'{line.Event}' property names changed. A new field needs a deliberate edit of " +
                    $"AllowedNames in this file — and a check that it cannot carry content. Line: {line.Message}")
                .That(actual).ContainsExactly(expected);
        }
    }

    [StaFact]
    public void EveryStringValue_IsFromAClosedVocabulary_AndTheRestAreNumbersOrBools()
    {
        var lines = CaptureSession();

        foreach (var line in lines)
        {
            foreach (var (name, value) in line.Props)
            {
                switch (value)
                {
                    case int or long or bool:
                        break;
                    case string s:
                        Check.WithCustomMessage($"String property '{name}' has no closed vocabulary: {line.Message}")
                            .That(AllowedStrings.ContainsKey(name)).IsTrue();
                        Check.WithCustomMessage($"Property {name}='{s}' is outside its vocabulary: {line.Message}")
                            .That(AllowedStrings[name].Contains(s)).IsTrue();
                        break;
                    default:
                        Check.WithCustomMessage(
                                $"Property '{name}' is a {value?.GetType().Name ?? "null"}: only ints, bools and " +
                                $"vocabulary strings may be logged (a double also renders with the current culture): {line.Message}")
                            .That(false).IsTrue();
                        break;
                }
            }
        }
    }

    [StaFact]
    public void ALetterDigitSymbolOrEmojiLine_CarriesNoIdentityBoxPixelsSaccadeOrLikelihood_ButANonTextLineKeepsItsFacts()
    {
        var lines = CaptureSession();

        var textLines = lines.Where(l => l.Event is "key" or "abandon"
            && TextKeyClasses.Contains((string)l.Props["Class"]!)).ToList();
        var nonTextLines = lines.Where(l => l.Event is "key" or "abandon"
            && !TextKeyClasses.Contains((string)l.Props["Class"]!)).ToList();

        Check.WithCustomMessage("The session typed no text keys: this gate would pass over nothing.")
            .That(textLines.Count).IsStrictlyGreaterThan(5);
        foreach (var line in textLines)
            foreach (var banned in TextKeyBannedNames)
                Check.WithCustomMessage($"A text key's line names '{banned}' (identity, or a fingerprint of it): {line.Message}")
                    .That(line.Props.ContainsKey(banned)).IsFalse();
        foreach (var line in textLines)
        {
            Check.WithCustomMessage("A text line carries an edge-quarter class: " + line.Message)
                .That(line.Props.ContainsKey("EdgeQ")).IsFalse();
            Check.That(line.Message).Not.Contains(" edgeq=");
        }

        Check.That(nonTextLines.Count).IsStrictlyGreaterThan(2);
        foreach (var line in nonTextLines.Where(l => l.Event == "key"))
            foreach (var kept in IdentityNames)
                Check.WithCustomMessage($"A non-text key's line lost '{kept}': {line.Message}")
                    .That(line.Props.ContainsKey(kept)).IsTrue();
    }

    [StaFact]
    public void TheEdgeTally_CountsExactlyTheSessionsTextDwellsAndAbandonments_AndNothingElse()
    {
        var lines = CaptureSession();
        var tallies = lines.Where(l => l.Event == "edgeq").ToList();
        Check.WithCustomMessage("The session wrote no edge tally").That(tallies.Count).IsStrictlyGreaterThan(0);

        var textKeys = lines.Count(l => l.Event == "key" && TextKeyClasses.Contains((string)l.Props["Class"]!));
        var textAbandons = lines.Count(l => l.Event == "abandon" && TextKeyClasses.Contains((string)l.Props["Class"]!));
        Check.That(tallies.Sum(l => (int)l.Props["Fires"]!)).IsEqualTo(textKeys);
        Check.That(tallies.Sum(l => (int)l.Props["Aborts"]!)).IsEqualTo(textAbandons);
        foreach (var line in tallies)
        {
            var fires = new[] { "C", "N", "NE", "E", "SE", "S", "SW", "W", "NW" }.Sum(n => (int)line.Props[n]!);
            var aborts = new[] { "AC", "AN", "ANE", "AE", "ASE", "AS", "ASW", "AW", "ANW" }.Sum(n => (int)line.Props[n]!);
            Check.That(fires).IsEqualTo((int)line.Props["Fires"]!);
            Check.That(aborts).IsEqualTo((int)line.Props["Aborts"]!);
        }
        // Written before the hide, and before the flip's edge line: never after the line that ends a show.
        var order = lines.Select(l => l.Event).ToList();
        Check.That(order.LastIndexOf("edgeq")).IsStrictlyLessThan(order.LastIndexOf("hidden"));
    }

    [StaFact]
    public void TheLetterBoxIsLoggedOncePerLayout_NotOnEveryKey()
    {
        var lines = CaptureSession();

        Check.That(lines.Count(l => l.Event == "layout")).IsStrictlyGreaterThan(0);
        Check.That(lines.Count(l => l.Event == "layout")).IsStrictlyLessThan(lines.Count(l => l.Event == "key"));
        Check.That(lines.Single(l => l.Event == "shown").Props.ContainsKey("BoxW")).IsTrue();
    }

    [StaFact]
    public void TheSlipLine_DescribesTheRepairAndNamesNeitherKey()
    {
        var lines = CaptureSession();

        var slip = lines.Single(l => l.Event == "slip");
        // k -> l in the session: the key beside it, to the east, one pitch away.
        Check.That((bool)slip.Props["Adj"]!).IsTrue();
        Check.That((string)slip.Props["Dir"]!).IsEqualTo("E");
        Check.That((int)slip.Props["Dist"]!).IsEqualTo(1);
        Check.That(slip.Props.Keys.OrderBy(k => k, StringComparer.Ordinal))
            .ContainsExactly(AllowedNames["slip"].OrderBy(k => k, StringComparer.Ordinal).ToArray());
    }

    // ---- F1: a password field logs nothing per key ---------------------------------------------

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    [StaFact]
    public void ATypingSessionInASensitiveContext_LogsNoPerKeyLine_AndOnlyOneMarker()
    {
        var sensitive = new FakeSensitive { IsSensitiveActive = true };
        var p = Open(sensitive: sensitive);
        try
        {
            var typed = 0;
            p.Vm.KeyDwellCompleted += _ => typed++;
            long ts = 1_000;
            p.Vm.IsVisible = true;
            foreach (var letter in "geheim")
                Dwell(p, IdOf(p, letter.ToString()), ref ts, holdAfterUs: 100_000);
            Dwell(p, SpecialId(p, "backspace"), ref ts);
            Dwell(p, IdOf(p, "x"), ref ts);
            Dwell(p, SpecialId(p, "space"), ref ts);

            var lines = p.Log.All();
            Check.WithCustomMessage("The keyboard must still TYPE in a password field").That(typed).IsEqualTo(9);
            Check.WithCustomMessage("A password's keys were logged: " + string.Join(" | ", lines.Select(l => l.Message)))
                .That(lines.Select(l => l.Message)).ContainsExactly("KB-E1 sensitive");
            Check.That(lines.Single().Props.Count).IsEqualTo(0);   // no count, no field at all
        }
        finally { p.Dispose(); }
    }

    // ---- the log cannot spell the text --------------------------------------------------------

    /// <summary>
    /// The only fields excluded from a comparison by default: the sequence number and the absolute
    /// timestamp, which differ between two words by construction. The recorder runs on a fake clock, so
    /// every other timing field (<c>SinceKeyMs</c>, <c>SinceMoveMs</c>) is deterministic and is compared
    /// too: a column that leaked into <c>movems</c> or <c>sincekey</c> fails the gate. Everything must not
    /// depend on WHICH letter was hit, except the documented residual (hold-on, and the time since the
    /// previous key, which rides with a doubled letter's long stay).
    /// </summary>
    private static readonly string[] NoiseNames = ["Seq", "TsUs"];

    private static List<string> Signature(IEnumerable<Line> lines, params string[] alsoExcluded) =>
        lines.Select(l => l.Event + " " + string.Join(" ", l.Props
                .Where(p => !NoiseNames.Contains(p.Key) && !alsoExcluded.Contains(p.Key))
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => $"{p.Key}={Convert.ToString(p.Value, CultureInfo.InvariantCulture)}")))
            .ToList();

    private static readonly Dictionary<string, int> ReconstructionWords = new()
    {
        ["hund"] = 500, ["mond"] = 500, ["wert"] = 500, ["yxcv"] = 500, ["quap"] = 500, ["mnbv"] = 500,
        ["hallo"] = 500, ["haben"] = 500,
    };

    /// <summary>Gaze aimed a fixed fraction of the key box east of centre: every key reads the same edge quarter.</summary>
    private static PhysicalPoint Aim(Probe p, string keyId)
    {
        var centre = Centres(p.Window)[keyId];
        var box = Boxes(p.Window)[keyId];
        return new PhysicalPoint(centre.X + 0.3 * box.Width, centre.Y);
    }

    /// <summary>
    /// Looks away between keys (the old driver), but also glances at each word's first key long enough to
    /// abandon it, so abandon lines exist to be compared. Words end with Space.
    /// </summary>
    private static void TypeLookingAway(Probe p, string word, ref long ts)
    {
        var first = IdOf(p, word[0].ToString());
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var glance = Aim(p, first);
        for (var t = ts; t < ts + 300_000; t += 50_000)
            Gaze(p, glance, t);
        ts += 350_000;
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;

        foreach (var letter in word)
            Dwell(p, IdOf(p, letter.ToString()), ref ts, holdAfterUs: 100_000);
        Dwell(p, SpecialId(p, "space"), ref ts);
    }

    /// <summary>
    /// Goes key to key DIRECTLY (no look-away), aiming off-centre, holding on each key a little after the
    /// fire; a double letter is held long enough to fire twice. Ends with Space.
    /// </summary>
    private static void TypeKeyToKey(Probe p, string word, ref long ts)
    {
        p.Content.UpdateLayout();
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var centres = Centres(p.Window);          // refreshed by the sample above
        for (var i = 0; i < word.Length; i++)
        {
            var aim = Aim(p, IdOf(p, word[i].ToString()));
            var double_ = i + 1 < word.Length && word[i + 1] == word[i];
            var heldUs = double_ ? 2 * DwellUs + 200_000 : DwellUs + 100_000;
            var start = ts;
            for (var t = start; t <= start + heldUs; t += 50_000)
                Gaze(p, aim, t);
            ts = start + heldUs + 50_000;
            if (double_) i++;                     // the second letter was typed by the same stay
        }
        var space = Aim(p, SpecialId(p, "space"));
        var from = ts;
        for (var t = from; t <= from + DwellUs; t += 50_000)
            Gaze(p, space, t);
        ts = from + DwellUs + 50_000;
        // Look away so the Space's hold-on is logged inside its own word.
        Gaze(p, new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        _ = centres;
    }

    private static List<Line> LinesOfWord(List<Line> all, int wordIndex, int wordCount)
    {
        // Each word ends with its Space key line; split the day's lines at those.
        var parts = new List<List<Line>> { new() };
        object? lastSpaceSeq = null;
        foreach (var line in all)
        {
            // The Space's own hold-on is logged after its key line (when the gaze leaves): it belongs
            // to the word the Space ended.
            if (line.Event == "holdon" && lastSpaceSeq is not null && Equals(line.Props["Seq"], lastSpaceSeq))
            {
                parts[^2].Add(line);
                continue;
            }
            parts[^1].Add(line);
            if (line.Event == "key" && (string)line.Props["Class"]! == "space")
            {
                lastSpaceSeq = line.Props["Seq"];
                parts.Add(new List<Line>());
            }
        }
        return parts[wordIndex];
    }

    [StaTheory]
    [InlineData("hund", "mond", 1.0, 1.0)]
    [InlineData("wert", "yxcv", 1.0, 1.0)]      // top row against bottom row: the row would be the loudest tell
    [InlineData("quap", "mnbv", 1.0, 1.0)]
    [InlineData("hund", "mond", 1.5, 1.5)]      // fractional scale: letter boxes differ by a pixel per column
    [InlineData("wert", "yxcv", 1.5, 1.5)]
    [InlineData("quap", "mnbv", 1.5, 1.5)]
    public void TwoDifferentWordsOfTheSameLength_LeaveIndistinguishableLines(
        string first, string second, double scale, double keyMultiplier)
    {
        // Only clock values are excluded. Every key, abandon and hold-on line of the two words must
        // match field for field: nothing may depend on which letter was hit.
        var p = Open(ReconstructionWords, scale, keyMultiplier);
        try
        {
            long ts = 1_000;
            p.Vm.IsVisible = true;
            TypeLookingAway(p, "hallo", ref ts);       // warm-up: both compared words then follow a Space
            TypeLookingAway(p, first, ref ts);
            TypeLookingAway(p, second, ref ts);

            var all = p.Log.All().Where(l => l.Event is "key" or "abandon" or "holdon").ToList();
            var a = LinesOfWord(all, 1, 3);
            var b = LinesOfWord(all, 2, 3);
            Check.WithCustomMessage("The driver produced no key lines").That(a.Count(l => l.Event == "key"))
                .IsEqualTo(first.Length + 1);
            Check.That(a.Count(l => l.Event == "abandon")).IsStrictlyGreaterThan(0);
            Check.That(a.Count(l => l.Event == "holdon")).IsStrictlyGreaterThan(0);

            foreach (var line in a.Concat(b).Where(l => l.Event is "key" or "abandon"
                         && TextKeyClasses.Contains((string)l.Props["Class"]!)))
                foreach (var banned in TextKeyBannedNames)
                    Check.That(line.Props.ContainsKey(banned)).IsFalse();

            Check.WithCustomMessage($"'{first}' and '{second}' are distinguishable (scale {scale}, keys {keyMultiplier})")
                .That(Signature(b)).ContainsExactly(Signature(a).ToArray());

            foreach (var line in p.Log.All())
                foreach (var word in new[] { first, second })
                    Check.That(line.Message.ToLowerInvariant()).Not.Contains(word);
        }
        finally { p.Dispose(); }
    }

    [StaFact]
    public void ABrokenLogger_NeverSwallowsABarPick()
    {
        // KeyboardWindow.RecordPickE1 runs BEFORE the selection. A logging stack that throws (here
        // IsEnabled) must reach its catch and leave the pick working.
        var p = Open();
        try
        {
            long ts = 1_000;
            p.Vm.IsVisible = true;
            Dwell(p, IdOf(p, "z"), ref ts);
            Dwell(p, IdOf(p, "e"), ref ts);
            Dwell(p, IdOf(p, "b"), ref ts);
            Check.That(p.Vm.CurrentInput).IsEqualTo("zeb");

            p.Log.ThrowOnIsEnabled = true;
            try { p.Window.OnPredictionDwelled("pred_0"); }
            finally { p.Log.ThrowOnIsEnabled = false; }

            Check.WithCustomMessage("the bar pick did not happen")
                .That(p.Vm.CurrentInput).IsNotEqualTo("zeb");
        }
        finally { p.Dispose(); }
    }

    [StaTheory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.5, 1.5)]
    public void DirectKeyToKeyTyping_WithAHoldOnAndADoubleLetter_DiffersOnlyInTheDocumentedResidual(
        double scale, double keyMultiplier)
    {
        // "hallo" has a double letter, "haben" has none. Going key to key directly (no look-away) and
        // holding on each key, the ONLY thing that may tell them apart is the hold-on of the doubled
        // key (and the timing that rides with it). Every key line is identical, field for field.
        var p = Open(ReconstructionWords, scale, keyMultiplier);
        try
        {
            long ts = 1_000;
            p.Vm.IsVisible = true;
            TypeKeyToKey(p, "hallo", ref ts);
            TypeKeyToKey(p, "haben", ref ts);

            var all = p.Log.All().Where(l => l.Event is "key" or "abandon" or "holdon").ToList();
            var doubled = LinesOfWord(all, 0, 2);
            var plain = LinesOfWord(all, 1, 2);

            var doubledKeys = doubled.Where(l => l.Event == "key").ToList();
            var plainKeys = plain.Where(l => l.Event == "key").ToList();
            Check.That(doubledKeys).HasSize(6);             // h a l l o + Space
            Check.That(plainKeys).HasSize(6);

            // 1. Key lines: identical, field for field (noise = clock values only).
            // (sincekey is the one excluded timing field: the doubled key's second fire follows its first
            // after the long stay, which is the documented residual below.)
            Check.WithCustomMessage("hallo and haben are distinguishable from their KEY lines going key to key")
                .That(Signature(plainKeys, "SinceKeyMs")).ContainsExactly(Signature(doubledKeys, "SinceKeyMs").ToArray());

            // 2. The residual: the doubled key's hold-on is the long stay between its two fires.
            var doubledHolds = doubled.Where(l => l.Event == "holdon").Select(l => (int)l.Props["Ms"]!).ToList();
            var plainHolds = plain.Where(l => l.Event == "holdon").Select(l => (int)l.Props["Ms"]!).ToList();
            Check.That(doubledHolds.Max()).IsStrictlyGreaterThan(400);
            Check.That(plainHolds.Max()).IsStrictlyLessThan(250);

            // 3. Nothing else differs: with the hold-on durations removed the hold-on lines match too.
            Check.That(doubled.Count(l => l.Event == "holdon")).IsEqualTo(plain.Count(l => l.Event == "holdon"));
            foreach (var line in doubled.Concat(plain).Where(l => l.Event == "key"
                         && TextKeyClasses.Contains((string)l.Props["Class"]!)))
                foreach (var banned in TextKeyBannedNames)
                    Check.That(line.Props.ContainsKey(banned)).IsFalse();
        }
        finally { p.Dispose(); }
    }

    [Fact]
    public void TheContextProbeInterface_OffersNoWindowTitle_AndNoOtherWayToReadOnscreenText()
    {
        // The owner's condition names window titles. The structural guarantee is that the seam the
        // log reads through cannot return one: exactly a process-name read and a caret-tail read.
        var members = typeof(IKeyboardContextProbe).GetMembers()
            .Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Check.That(members).ContainsExactly("GetForegroundProcessName", "ReadFocusedTextTail");
    }

    [StaFact]
    public void TheMirrorChecksVerdictsAreLengthsOnly_EvenWhenTheControlReturnsTheTypedText()
    {
        var lines = CaptureSession();

        var mirror = lines.Where(l => l.Event == "mirror").ToList();
        Check.That(mirror).HasSize(2);
        foreach (var l in mirror)
            Check.That(l.Props.Keys.OrderBy(k => k, StringComparer.Ordinal)).ContainsExactly(
                "At", "Common", "CtlLen", "MirrorLen", "Res");
    }

    // ---- the real day-log rendering ---------------------------------------------------------

    [Fact]
    public void ThroughSerilog_TheLineRendersWithIntegersOnly_EvenUnderAGermanCulture()
    {
        // The day log renders {Message:lj}. A double would render "12,5" under de-DE; a bool renders
        // as true/false (the JSON form), strings unquoted. The parser reads exactly this shape.
        var sink = new CollectingSink();
        using var root = new Serilog.LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(sink)
            .CreateLogger();
        using var factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(root);
        var recorder = new KeyboardE1Recorder(factory.CreateLogger<KeyboardViewModel>(), () => 0);

        recorder.RecordKey(new KeyE1(
            1_234_567, 1, 2, KeyE1.Letter, "-", 210, 210, "letters", "top", 500, 0, 2800, 1000, 7000,
            -3, 4, -2, 5, "NE", 1, "mid", 450, false, true, "-"));
        recorder.RecordKey(new KeyE1(
            1_300_000, 3, 9, KeyE1.Backspace, "backspace", 210, 210, "letters", "top", 500, 0, 2800, 1000, 7000,
            0, 0, 0, 0, "-", 0, "long", 450, false, true, "word"));

        // Exactly the day log's {Message:lj} (literal strings, JSON-ish scalars), under German.
        var formatter = new Serilog.Formatting.Display.MessageTemplateTextFormatter(
            "{Message:lj}", CultureInfo.GetCultureInfo("de-DE"));
        using var writer = new StringWriter();
        formatter.Format(sink.Events[0], writer);
        var rendered = writer.ToString();
        var renderedBackspace = Render(formatter, sink.Events[1]);
        Check.That(rendered).IsEqualTo(
            "KB-E1 key seq=1 ts=1234567 cls=letter sub=letters edge=top winx=500 winy=0 winw=2800 winh=1000 " +
            "movems=7000 sincekey=-1 dwell=450 penalty=false typing=true bs=-");
        // The same line for a NON-text key keeps its position and id.
        Check.That(renderedBackspace).IsEqualTo(
            "KB-E1 key seq=2 ts=1300000 r=3 c=9 cls=backspace act=backspace boxw=210 boxh=210 sub=letters edge=top " +
            "winx=500 winy=0 winw=2800 winh=1000 movems=7000 dx=0 dy=0 mdx=0 mdy=0 restarts=0 saccade=long " +
            "sincekey=0 dwell=450 penalty=false typing=true bs=word");
    }

    private static string Render(Serilog.Formatting.Display.MessageTemplateTextFormatter formatter,
        Serilog.Events.LogEvent logEvent)
    {
        using var writer = new StringWriter();
        formatter.Format(logEvent, writer);
        return writer.ToString();
    }

    private sealed class CollectingSink : Serilog.Core.ILogEventSink
    {
        public readonly List<Serilog.Events.LogEvent> Events = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }
}
