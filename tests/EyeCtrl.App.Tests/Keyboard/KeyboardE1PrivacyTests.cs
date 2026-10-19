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

    private static readonly Dictionary<string, string[]> AllowedNames = new()
    {
        ["key"] = ["Seq", "TsUs", "Row", "Col", "Class", "Act", "BoxW", "BoxH", "Sub", "Edge", "WinX", "WinY",
                   "WinW", "WinH", "SinceMoveMs", "Dx", "Dy", "MeanDx", "MeanDy", "Restarts", "SaccadePx",
                   "SinceKeyMs", "DwellMs", "Penalty", "Typing", "Bs", "Likely", "Start", "NoCont"],
        ["holdon"] = ["Seq", "Ms", "Capped"],
        ["abandon"] = ["TsUs", "Row", "Col", "Class", "Act", "Pct", "Why", "Dx", "Dy", "Sub", "Edge"],
        ["pick"] = ["Slot", "Source", "PrefixLen", "InsLen", "SinceKeyMs", "SincePickMs", "RenderMs", "Visited"],
        ["pickundo"] = ["Ms", "By"],
        ["space"] = ["Seq", "Empty"],
        ["mode"] = ["Kind", "From", "To"],
        ["modeundo"] = ["Kind", "Ms"],
        ["edge"] = ["Button", "Edge"],
        ["shown"] = ["Proc", "Edge", "Via"],
        ["hidden"] = ["Cause", "VisibleMs"],
        ["mirror"] = ["At", "Res", "MirrorLen", "CtlLen", "Common"],
    };

    /// <summary>The closed vocabulary of every string-valued property, by property name.</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedStrings = new()
    {
        ["Class"] = ["letter", "digit", "space", "backspace", "deleteword", "enter", "shift", "caps", "mode",
                     "switchsub", "emoji", "other"],
        ["Act"] = ["-", "space", "backspace", "deleteWord", "enter", "shift", "modifier:shift", "toggle:capslock",
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

    // ---- capture ----------------------------------------------------------------------------

    private sealed record Line(string Event, string Message, IReadOnlyDictionary<string, object?> Props);

    private sealed class Capture : ILogger<KeyboardViewModel>
    {
        private readonly object _gate = new();
        private readonly List<Line> _lines = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

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

    private sealed record Probe(
        KeyboardWindow Window, KeyboardViewModel Vm, FrameworkElement Content, HwndSource Source, Capture Log,
        KeyboardLayout Layout) : IDisposable
    {
        public void Dispose() { Source.Dispose(); Window.Close(); }
    }

    private static Dictionary<string, PhysicalPoint> Centres(KeyboardWindow w) =>
        (Dictionary<string, PhysicalPoint>)typeof(KeyboardWindow)
            .GetField("_keyScreenPositions", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(w)!;

    private static Probe Open()
    {
        WpfApplicationGate.EnsureApplicationWithTokens();
        var log = new Capture();
        var settings = new EyeCtrlSettings();
        settings.Keyboard.SmartCapitalizationEnabled = false;
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger(),
            logger: log, contextProbe: new SpyProbe());
        vm.SetOrchestrator(new PredictionOrchestrator(new WordPredictor(
            new InMemoryDictProvider(new Dictionary<string, int>
            {
                ["zebrafink"] = 900, ["zebra"] = 500, ["hallo"] = 700, ["welt"] = 300,
            }), "QWERTZ")));

        var layout = KeyboardLayoutLoader.Load(Path.Combine(
            SourceScanner.RepoRoot().FullName, "src", "EyeCtrl.App", "Resources", "Layouts", "qwertz.json"));

        var w = new KeyboardWindow();
        w.SetViewModel(vm);
        w.LoadLayout(layout, 1.0, keyDwellMs: DwellMs, predictionDwellMs: 800, cooldownMs: 0);
        w.SetSettings(settings);

        var content = (FrameworkElement)w.Content;
        w.Content = null;
        var source = new HwndSource(new HwndSourceParameters("kb-e1-privacy-probe")
        {
            WindowStyle = WsPopup, PositionX = -30000, PositionY = -30000, Width = 1, Height = 1,
        });
        var factor = 1.0 / source.CompositionTarget!.TransformToDevice.M11;
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
        return new Probe(w, vm, content, source, log, layout);
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
        p.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var centre = Centres(p.Window)[keyId];
        var latch = ts;
        p.Window.UpdateGaze(centre, latch); ts += 1_000;
        for (var t = latch + 50_000; t < latch + dwellUs; t += 50_000)
            p.Window.UpdateGaze(centre, t);
        p.Window.UpdateGaze(centre, latch + dwellUs);
        ts = latch + dwellUs + 1_000;
        if (holdAfterUs > 0)
        {
            for (var t = ts; t < ts + holdAfterUs; t += 50_000)
                p.Window.UpdateGaze(centre, t);
            ts += holdAfterUs;
        }
        p.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
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
        Dwell(p, SpecialId(p, "backspace"), ref ts);

        // An abandoned dwell: look at a key long enough to count, then away.
        var centres = Centres(p.Window);
        p.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;
        var start = ts;
        for (var t = start; t < start + 300_000; t += 50_000)
            p.Window.UpdateGaze(centres[IdOf(p, "f")], t);
        ts = start + 350_000;
        p.Window.UpdateGaze(new PhysicalPoint(-99999, -99999), ts); ts += 1_000;

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
                     "mirror", "pickundo",
                 })
        {
            Check.WithCustomMessage($"The typing session produced no '{expected}' line ({lines.Count} E1 lines in all).")
                .That(seen.Contains(expected)).IsTrue();
        }

        // z e b, Space, k, two Backspaces, Enter.
        Check.That(lines.Count(l => l.Event == "key")).IsEqualTo(8);
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
            var expected = AllowedNames[line.Event].OrderBy(n => n, StringComparer.Ordinal).ToArray();
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
            -3, 4, -2, 5, 1, 640, 450, false, true, "-", true, false, false));

        // Exactly the day log's {Message:lj} (literal strings, JSON-ish scalars), under German.
        var formatter = new Serilog.Formatting.Display.MessageTemplateTextFormatter(
            "{Message:lj}", CultureInfo.GetCultureInfo("de-DE"));
        using var writer = new StringWriter();
        formatter.Format(sink.Events.Single(), writer);
        var rendered = writer.ToString();
        Check.That(rendered).IsEqualTo(
            "KB-E1 key seq=1 ts=1234567 r=1 c=2 cls=letter act=- boxw=210 boxh=210 sub=letters edge=top " +
            "winx=500 winy=0 winw=2800 winh=1000 movems=7000 dx=-3 dy=4 mdx=-2 mdy=5 restarts=1 saccade=640 " +
            "sincekey=-1 dwell=450 penalty=false typing=true bs=- likely=true start=false nocont=false");
    }

    private sealed class CollectingSink : Serilog.Core.ILogEventSink
    {
        public readonly List<Serilog.Events.LogEvent> Events = new();
        public void Emit(Serilog.Events.LogEvent logEvent) => Events.Add(logEvent);
    }
}
