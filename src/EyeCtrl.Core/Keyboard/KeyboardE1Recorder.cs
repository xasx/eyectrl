namespace EyeCtrl.Core.Keyboard;

using System.Diagnostics;
using EyeCtrl.Core.Prediction;
using Microsoft.Extensions.Logging;

/// <summary>
/// E1: the keyboard's content-free event log. One <c>Information</c> line per EVENT (a key
/// activation, an abandonment, a bar pick, a mode change, a show or hide) in the ordinary day
/// log, every line starting <c>KB-E1 &lt;event&gt;</c> so <c>tools/kb_log_metrics.py</c> can find it.
///
/// <para>
/// <b>Privacy is the contract, and it is the merge gate.</b> A line carries geometry, key
/// CLASSES and layout/action ids, timings, counts and lengths, and the foreground PROCESS name —
/// never a typed character, a word, a prediction or a window title. Every value logged is an
/// integer, a bool, or one of a closed set of ids, so there is no free-text parameter to leak
/// through, and integers also dodge the culture trap (Serilog renders a double with the current
/// culture, and this owner's is German). The property-name set of each event is pinned by an
/// allow-list in <c>KeyboardE1PrivacyTests</c>, so a new field needs a deliberate test edit.
/// </para>
///
/// <para>
/// Not a per-sample logger: every call here is per EVENT. The recorder also owns the small
/// cross-event state the derived events need (the previous key and pick, the last mode change
/// for mode-undo, the pick awaiting a possible undo).
/// </para>
/// </summary>
public sealed class KeyboardE1Recorder
{
    /// <summary>The fixed first token of every E1 line.</summary>
    public const string Prefix = "KB-E1";

    /// <summary>A Backspace/DeleteWord this soon after a pick counts as undoing it.</summary>
    public const int PickUndoWindowMs = 3000;

    /// <summary>A mode change reversed this soon counts as undone.</summary>
    public const int ModeUndoWindowMs = 5000;

    private readonly ILogger? _logger;
    private readonly Func<long> _clockMs;

    private long _seq;
    private long _lastKeyMs = long.MinValue;
    private long _lastPickMs = long.MinValue;
    private bool _pickAwaitingUndo;
    private ModeChange? _lastMode;
    private long _shownAtMs = long.MinValue;

    private readonly record struct ModeChange(string Kind, string From, string To, long AtMs);

    /// <param name="logger">The logger the lines go to; null makes every call a no-op.</param>
    /// <param name="clockMs">A monotonic millisecond clock; injected so tests are deterministic.</param>
    public KeyboardE1Recorder(ILogger? logger, Func<long>? clockMs = null)
    {
        _logger = logger;
        _clockMs = clockMs ?? MonotonicMs;
    }

    /// <summary>A recorder that records nothing.</summary>
    public static KeyboardE1Recorder Disabled { get; } = new(null);

    /// <summary>The recorder's own monotonic time, in ms.</summary>
    public long NowMs => _clockMs();

    /// <summary>False when there is no logger or Information is filtered out.</summary>
    public bool Enabled => _logger is not null && _logger.IsEnabled(LogLevel.Information);

    private static long MonotonicMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>One key activation. Returns the line's sequence number (follow-up lines carry it).</summary>
    public long RecordKey(in KeyE1 k)
    {
        var now = _clockMs();
        var seq = ++_seq;
        var sinceKey = _lastKeyMs == long.MinValue ? -1 : now - _lastKeyMs;

        if (k.Class is KeyE1.Backspace or KeyE1.DeleteWord)
        {
            if (_pickAwaitingUndo && now - _lastPickMs <= PickUndoWindowMs && Enabled)
            {
                _pickAwaitingUndo = false;
                _logger!.LogInformation(
                    "KB-E1 pickundo ms={Ms} by={By}", now - _lastPickMs, k.Class);
            }
        }
        else if (k.Class is KeyE1.Letter or KeyE1.Digit or KeyE1.Space)
        {
            // Text typed in the new mode shows the mode was wanted: no later reversal is an undo.
            _lastMode = null;
        }

        _lastKeyMs = now;
        if (!Enabled) return seq;

        _logger!.LogInformation(
            "KB-E1 key seq={Seq} ts={TsUs} r={Row} c={Col} cls={Class} act={Act} boxw={BoxW} boxh={BoxH} " +
            "sub={Sub} edge={Edge} winx={WinX} winy={WinY} winw={WinW} winh={WinH} movems={SinceMoveMs} " +
            "dx={Dx} dy={Dy} mdx={MeanDx} mdy={MeanDy} restarts={Restarts} saccade={SaccadePx} " +
            "sincekey={SinceKeyMs} dwell={DwellMs} penalty={Penalty} typing={Typing} bs={Bs} " +
            "likely={Likely} start={Start} nocont={NoCont}",
            seq, k.TimestampUs, k.Row, k.Col, k.Class, k.Action, k.BoxWidthPx, k.BoxHeightPx,
            k.SubLayout, k.Edge, k.WindowX, k.WindowY, k.WindowWidth, k.WindowHeight, k.SinceWindowMoveMs,
            k.DxPx, k.DyPx, k.MeanDxPx, k.MeanDyPx, k.Restarts, k.SaccadePx,
            sinceKey, k.EffectiveDwellMs, k.PenaltyApplied, k.Typing, k.BackspaceClass,
            k.Likely, k.WordStart, k.NoContinuation);
        return seq;
    }

    /// <summary>How long the gaze stayed in the activated key's box after the fire (capped).</summary>
    public void RecordHoldOn(long seq, int ms, bool capped)
    {
        if (!Enabled) return;
        _logger!.LogInformation("KB-E1 holdon seq={Seq} ms={Ms} capped={Capped}", seq, ms, capped);
    }

    /// <summary>An abandoned dwell at or above <c>TelemetryThresholds.AbandonProgress</c>.</summary>
    public void RecordAbandon(in AbandonE1 a)
    {
        if (!Enabled) return;
        _logger!.LogInformation(
            "KB-E1 abandon ts={TsUs} r={Row} c={Col} cls={Class} act={Act} pct={Pct} why={Why} dx={Dx} dy={Dy} " +
            "sub={Sub} edge={Edge}",
            a.TimestampUs, a.Row, a.Col, a.Class, a.Action, a.ProgressPct, a.Reason, a.DxPx, a.DyPx,
            a.SubLayout, a.Edge);
    }

    /// <summary>One bar pick. Lengths and ids only; the picked word is never passed in.</summary>
    public void RecordPick(int slot, string source, int prefixLength, int insertedLength,
        int renderToDwellMs, int slotsVisited)
    {
        var now = _clockMs();
        var sinceKey = _lastKeyMs == long.MinValue ? -1 : now - _lastKeyMs;
        var sincePick = _lastPickMs == long.MinValue ? -1 : now - _lastPickMs;
        _lastPickMs = now;
        _pickAwaitingUndo = true;
        if (!Enabled) return;
        _logger!.LogInformation(
            "KB-E1 pick slot={Slot} src={Source} prefixlen={PrefixLen} inslen={InsLen} sincekey={SinceKeyMs} " +
            "sincepick={SincePickMs} renderms={RenderMs} visited={Visited}",
            slot, source, prefixLength, insertedLength, sinceKey, sincePick, renderToDwellMs, slotsVisited);
    }

    /// <summary>After a Space: was the bar empty?</summary>
    public void RecordSpace(long seq, bool barEmpty)
    {
        if (!Enabled) return;
        _logger!.LogInformation("KB-E1 space seq={Seq} empty={Empty}", seq, barEmpty);
    }

    /// <summary>
    /// A mode change: a sub-layout switch, emoji enter/exit, or a modifier toggle (explicit, not
    /// an auto-return or a one-shot consumed by a key). Detects a mode-undo.
    /// </summary>
    public void RecordMode(string kind, string from, string to)
    {
        if (Enabled)
            _logger!.LogInformation("KB-E1 mode kind={Kind} from={From} to={To}", kind, from, to);
        NoteModeChange(kind, from, to);
    }

    /// <summary>An edge-bar button (<c>flip</c>/<c>close</c>); a Flip also feeds mode-undo.</summary>
    public void RecordEdge(string button, string edgeAfter, string edgeBefore)
    {
        if (Enabled)
            _logger!.LogInformation("KB-E1 edge button={Button} edge={Edge}", button, edgeAfter);
        if (button == "flip")
            NoteModeChange("flip", edgeBefore, edgeAfter);
    }

    private void NoteModeChange(string kind, string from, string to)
    {
        var now = _clockMs();
        if (_lastMode is { } last
            && last.Kind == kind
            && last.To == from && last.From == to
            && now - last.AtMs <= ModeUndoWindowMs)
        {
            if (Enabled)
                _logger!.LogInformation(
                    "KB-E1 modeundo kind={Kind} ms={Ms}", kind, now - last.AtMs);
            _lastMode = null;   // an undo is not itself undoable
            return;
        }
        _lastMode = new ModeChange(kind, from, to, now);
    }

    /// <summary>The keyboard was shown. <paramref name="process"/> is the foreground PROCESS name, never a title.</summary>
    public void RecordShown(string process, string edge, string via)
    {
        _shownAtMs = _clockMs();
        if (!Enabled) return;
        _logger!.LogInformation("KB-E1 shown proc={Proc} edge={Edge} via={Via}", process, edge, via);
    }

    /// <summary>The keyboard was hidden, and why.</summary>
    public void RecordHidden(string cause)
    {
        var now = _clockMs();
        var visibleMs = _shownAtMs == long.MinValue ? -1 : now - _shownAtMs;
        _shownAtMs = long.MinValue;
        _lastMode = null;
        _pickAwaitingUndo = false;
        if (!Enabled) return;
        _logger!.LogInformation("KB-E1 hidden cause={Cause} visiblems={VisibleMs}", cause, visibleMs);
    }

    /// <summary>The mirror check's verdict: an outcome and lengths, never a string.</summary>
    public void RecordMirror(string at, string result, int mirrorLength, int controlLength, int commonSuffix)
    {
        if (!Enabled) return;
        _logger!.LogInformation(
            "KB-E1 mirror at={At} res={Res} mirrorlen={MirrorLen} ctllen={CtlLen} common={Common}",
            at, result, mirrorLength, controlLength, commonSuffix);
    }

    /// <summary>The closed set of bar sources. What each can and cannot tell is on <see cref="PredictionType"/>.</summary>
    public static string PickSource(PredictionType type) => type switch
    {
        PredictionType.ExactPrefix => "dict",
        PredictionType.FuzzyMatch => "fuzzy",
        PredictionType.NextWord => "pair",
        PredictionType.Phrase => "phrase",
        PredictionType.SentenceCompletion => "sentence",
        PredictionType.LMSuggestion => "lm",
        _ => "other",
    };
}

/// <summary>Everything E1 logs for one key activation. Ints, bools and closed ids only.</summary>
public readonly record struct KeyE1(
    long TimestampUs,
    int Row, int Col,
    string Class, string Action,
    int BoxWidthPx, int BoxHeightPx,
    string SubLayout, string Edge,
    int WindowX, int WindowY, int WindowWidth, int WindowHeight,
    long SinceWindowMoveMs,
    int DxPx, int DyPx, int MeanDxPx, int MeanDyPx,
    int Restarts, int SaccadePx,
    int EffectiveDwellMs, bool PenaltyApplied,
    bool Typing, string BackspaceClass,
    bool Likely, bool WordStart, bool NoContinuation)
{
    /// <summary>Class: a letter key.</summary>
    public const string Letter = "letter";
    /// <summary>Class: a digit key.</summary>
    public const string Digit = "digit";
    /// <summary>Class: Space.</summary>
    public const string Space = "space";
    /// <summary>Class: Backspace.</summary>
    public const string Backspace = "backspace";
    /// <summary>Class: word delete.</summary>
    public const string DeleteWord = "deleteword";
    /// <summary>Class: Enter.</summary>
    public const string Enter = "enter";
    /// <summary>Class: Shift.</summary>
    public const string Shift = "shift";
    /// <summary>Class: Caps Lock.</summary>
    public const string Caps = "caps";
    /// <summary>Class: another mode key (Ctrl, Alt, Win, the emoji switch).</summary>
    public const string Mode = "mode";
    /// <summary>Class: a sub-layout switch key.</summary>
    public const string SwitchSub = "switchsub";
    /// <summary>Class: any key while the emoji surface is up.</summary>
    public const string Emoji = "emoji";
    /// <summary>Class: everything else (punctuation, Tab, shortcuts, function keys).</summary>
    public const string Other = "other";
}

/// <summary>Everything E1 logs for one abandonment.</summary>
public readonly record struct AbandonE1(
    long TimestampUs, int Row, int Col, string Class, string Action,
    int ProgressPct, string Reason, int DxPx, int DyPx, string SubLayout, string Edge);

/// <summary>
/// The window facts a key line carries and the view-model cannot see: the dock edge, the window's
/// physical rect, and the time since the window last moved (a Flip or a toolbar yield — a moved
/// window silently re-maps a resting gaze onto another key). Supplied by the window.
/// </summary>
public readonly record struct KeyboardE1Geometry(
    string Edge, int X, int Y, int Width, int Height, long SinceMoveMs)
{
    /// <summary>What a key line carries when no window supplies geometry.</summary>
    public static KeyboardE1Geometry Unknown { get; } = new("-", 0, 0, 0, 0, -1);
}
