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
/// <b>A text key's line says only "a text key was typed".</b> For a letter, digit, symbol or
/// emoji key the line carries NO row, column, action id, label, box size or pixel offset, and no
/// per-key saccade, restart count or likelihood: each of those either IS the key's position or
/// leaks it (the tracker's bias field turns a pixel offset into a column; box sizes differ by one
/// pixel per column at fractional display scales; a saccade class or a likelihood bit narrows the
/// word). What remains is timing, the dock edge, whether a word was open, and — on a separate keyed
/// line — the hold-on time. Which outer quarter of the box the gaze rested in is NOT on the line
/// either (a per-letter class plus the tracker's bias field would let a reader narrow the word): it
/// is only COUNTED, per dock edge, in one <c>KB-E1 edgeq</c> tally line (see <see cref="FlushEdgeTally"/>), and
/// that line is written only once the edge has <see cref="EdgeTallyMinFires"/> dwells; smaller counts are
/// carried to the edge's next show. The things a correction needs about the SLIPPED
/// key are logged only when it is corrected (<see cref="RecordCorrection"/>, <see cref="RecordSlip"/>).
/// Only non-text keys (Space, Backspace, Enter, modifiers, sub-layout switches, shortcuts) keep
/// their position and id, because they do not spell anything. The recorder enforces all of this
/// itself, whatever the caller passes.
/// </para>
///
/// <para>
/// <b>Nothing at all in a password field.</b> While the injected sensitivity probe reports a
/// sensitive context, no key, abandon, hold-on, slip, correction, pick, pick-undo, space or mode
/// line is written (a password's length, composition and order would be in them); one
/// <c>KB-E1 sensitive</c> marker with no count is written when the context is entered.
/// </para>
///
/// <para>
/// Not a per-sample logger: every call here is per EVENT. The recorder also owns the small
/// cross-event state the derived events need (the previous key and pick, the last mode change
/// for mode-undo, the pick awaiting a possible undo). Logging must never throw into the keyboard:
/// every write is guarded.
/// </para>
/// </summary>
public sealed class KeyboardE1Recorder
{
    /// <summary>The fixed first token of every E1 line.</summary>
    public const string Prefix = "KB-E1";

    /// <summary>A Backspace/DeleteWord this soon after a pick counts as undoing it.</summary>
    public const int PickUndoWindowMs = 3000;

    /// <summary>A Backspace this soon after a letter/digit, inside the word, marks it a slip.</summary>
    public const int SlipWindowMs = 3000;

    /// <summary>A mode change reversed this soon counts as undone.</summary>
    public const int ModeUndoWindowMs = 5000;

    /// <summary>
    /// A dock edge's tally line is written only once its accumulated counts reach this many accepted
    /// text-key dwells. A tally from a one-word show still narrows that word a little (a modelled
    /// reader guesses the top word 10-15 % of the time against 7.9 % from the word's length alone);
    /// below the minimum the counts are CARRIED to that edge's next show instead, in memory only, and
    /// a leftover that never reaches it is dropped at shutdown, never written.
    /// </summary>
    public const int EdgeTallyMinFires = 20;

    private readonly ILogger? _logger;
    private readonly Func<long> _clockMs;

    private long _seq;
    private long _lastKeyMs = long.MinValue;
    private long _lastPickMs = long.MinValue;
    private bool _pickAwaitingUndo;
    private ModeChange? _lastMode;
    private long _shownAtMs = long.MinValue;
    private bool _inSensitive;

    private readonly record struct ModeChange(string Kind, string From, string To, long AtMs);

    /// <param name="logger">The logger the lines go to; null makes every call a no-op.</param>
    /// <param name="clockMs">A monotonic millisecond clock; injected so tests are deterministic.</param>
    /// <param name="isSensitive">Whether a password-like field has focus; null means never.</param>
    /// <param name="edgeTallyMinFires">The accepted text dwells an edge's tally needs before it is written
    /// (<see cref="EdgeTallyMinFires"/>; tests pass a smaller number to exercise the line format).</param>
    public KeyboardE1Recorder(ILogger? logger, Func<long>? clockMs = null, Func<bool>? isSensitive = null,
        int edgeTallyMinFires = EdgeTallyMinFires)
    {
        _logger = logger;
        _clockMs = clockMs ?? MonotonicMs;
        IsSensitive = isSensitive;
        _tallyMinFires = Math.Max(1, edgeTallyMinFires);
    }

    /// <summary>A recorder that records nothing.</summary>
    public static KeyboardE1Recorder Disabled { get; } = new(null);

    /// <summary>
    /// Reports whether a sensitive (password) context has focus. Assigned by the view-model from the
    /// injected <c>ISensitiveContextDetector</c>; a throwing probe counts as not sensitive.
    /// </summary>
    public Func<bool>? IsSensitive { get; set; }

    /// <summary>The recorder's own monotonic time, in ms.</summary>
    public long NowMs => _clockMs();

    private bool _tainted;

    /// <summary>
    /// True from the first muted (sensitive-context) call until the editing session ends. The keyboard's
    /// record of typed text may then hold what was typed into a password field (focus can leave the
    /// field by gaze, without any key the keyboard sees), so a mirror check must not report its length
    /// or read the control. Cleared by <see cref="ClearTaint"/>, which the view-model calls when the
    /// mirror is cleared.
    /// </summary>
    public bool Tainted => _tainted;

    /// <summary>The mirror was cleared: what it held can no longer be a password.</summary>
    public void ClearTaint() => _tainted = false;

    /// <summary>False when there is no logger or Information is filtered out.</summary>
    public bool Enabled => _logger is not null && _logger.IsEnabled(LogLevel.Information);

    private static long MonotonicMs() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    /// <summary>
    /// True while a sensitive context has focus: callers (and every recorder method below) must then
    /// log nothing per key. The first call after entering drops the pending pick-undo and mode-undo
    /// state and writes ONE <c>KB-E1 sensitive</c> marker with no count.
    /// </summary>
    public bool Muted()
    {
        bool sensitive;
        try { sensitive = IsSensitive?.Invoke() == true; }
        catch (Exception) { sensitive = false; }

        if (sensitive) _tainted = true;

        if (sensitive && !_inSensitive)
        {
            _inSensitive = true;
            _pickAwaitingUndo = false;
            _lastMode = null;
            if (Enabled) Emit("KB-E1 sensitive");
        }
        else if (!sensitive)
        {
            _inSensitive = false;
        }
        return sensitive;
    }

    private void Emit(string template, params object?[] args)
    {
        try { _logger!.LogInformation(template, args); }
        catch (Exception) { /* a logging failure must never reach the keyboard */ }
    }

    /// <summary>
    /// One key activation. Returns the line's sequence number (follow-up lines carry it), or 0 when
    /// nothing was logged because a sensitive context has focus.
    /// </summary>
    public long RecordKey(in KeyE1 k)
    {
        if (Muted()) return 0;

        var now = _clockMs();
        var seq = ++_seq;
        var sinceKey = _lastKeyMs == long.MinValue ? -1 : now - _lastKeyMs;

        if (k.Class is KeyE1.Backspace or KeyE1.DeleteWord)
        {
            if (_pickAwaitingUndo && now - _lastPickMs <= PickUndoWindowMs && Enabled)
            {
                _pickAwaitingUndo = false;
                Emit("KB-E1 pickundo ms={Ms} by={By}", now - _lastPickMs, k.Class);
            }
        }
        else if (k.Class is KeyE1.Letter or KeyE1.Digit or KeyE1.Space)
        {
            // Text typed in the new mode shows the mode was wanted: no later reversal is an undo.
            _lastMode = null;
        }

        _lastKeyMs = now;
        if (!Enabled) return seq;

        if (IsIdentityFree(k.Class))
        {
            CountEdgeQuarter(k.Edge, k.EdgeQuarter, abandon: false);
            Emit(
                "KB-E1 key seq={Seq} ts={TsUs} cls={Class} sub={Sub} edge={Edge} winx={WinX} winy={WinY} " +
                "winw={WinW} winh={WinH} movems={SinceMoveMs} sincekey={SinceKeyMs} " +
                "dwell={DwellMs} penalty={Penalty} typing={Typing} bs={Bs}",
                seq, k.TimestampUs, k.Class, k.SubLayout, k.Edge, k.WindowX, k.WindowY, k.WindowWidth,
                k.WindowHeight, k.SinceWindowMoveMs, sinceKey, k.EffectiveDwellMs,
                k.PenaltyApplied, k.Typing, k.BackspaceClass);
            return seq;
        }

        Emit(
            "KB-E1 key seq={Seq} ts={TsUs} r={Row} c={Col} cls={Class} act={Act} boxw={BoxW} boxh={BoxH} " +
            "sub={Sub} edge={Edge} winx={WinX} winy={WinY} winw={WinW} winh={WinH} movems={SinceMoveMs} " +
            "dx={Dx} dy={Dy} mdx={MeanDx} mdy={MeanDy} restarts={Restarts} saccade={Saccade} " +
            "sincekey={SinceKeyMs} dwell={DwellMs} penalty={Penalty} typing={Typing} bs={Bs}",
            seq, k.TimestampUs, k.Row, k.Col, k.Class, k.Action, k.BoxWidthPx, k.BoxHeightPx,
            k.SubLayout, k.Edge, k.WindowX, k.WindowY, k.WindowWidth, k.WindowHeight, k.SinceWindowMoveMs,
            k.DxPx, k.DyPx, k.MeanDxPx, k.MeanDyPx, k.Restarts, k.Saccade,
            sinceKey, k.EffectiveDwellMs, k.PenaltyApplied, k.Typing, k.BackspaceClass);
        return seq;
    }

    /// <summary>
    /// What a correction needs about the key that was SLIPPED, logged only on the in-word Backspace
    /// that takes it back (keyed to that Backspace's <paramref name="seq"/>): how the gaze arrived,
    /// how many dwells were discarded, and the shadow likelihood bits. Never on an ordinary letter.
    /// </summary>
    public void RecordCorrection(long seq, in CorrE1 c)
    {
        if (seq <= 0 || Muted() || !Enabled) return;
        Emit(
            "KB-E1 corr seq={Seq} saccade={Saccade} restarts={Restarts} likely={Likely} start={Start} nocont={NoCont}",
            seq, c.Saccade, c.Restarts, c.Likely, c.WordStart, c.NoContinuation);
    }

    /// <summary>How long the gaze stayed in the activated key's box after the fire (capped).</summary>
    public void RecordHoldOn(long seq, int ms, bool capped)
    {
        if (seq <= 0 || Muted() || !Enabled) return;
        Emit("KB-E1 holdon seq={Seq} ms={Ms} capped={Capped}", seq, ms, capped);
    }

    /// <summary>An abandoned dwell at or above <c>TelemetryThresholds.AbandonProgress</c>.</summary>
    public void RecordAbandon(in AbandonE1 a)
    {
        if (Muted() || !Enabled) return;
        if (IsIdentityFree(a.Class))
        {
            CountEdgeQuarter(a.Edge, a.EdgeQuarter, abandon: true);
            Emit(
                "KB-E1 abandon ts={TsUs} cls={Class} pct={Pct} why={Why} sub={Sub} edge={Edge}",
                a.TimestampUs, a.Class, a.ProgressPct, a.Reason, a.SubLayout, a.Edge);
            return;
        }
        Emit(
            "KB-E1 abandon ts={TsUs} r={Row} c={Col} cls={Class} act={Act} pct={Pct} why={Why} mdx={MeanDx} " +
            "mdy={MeanDy} sub={Sub} edge={Edge}",
            a.TimestampUs, a.Row, a.Col, a.Class, a.Action, a.ProgressPct, a.Reason, a.MeanDxPx, a.MeanDyPx,
            a.SubLayout, a.Edge);
    }

    /// <summary>
    /// A slip and its repair: a letter or digit was followed by a Backspace inside the word and then
    /// by a replacement letter or digit. The line describes the RELATION between the two keys and
    /// identifies neither: whether the replacement is a geometric neighbour of the slipped key
    /// (<see cref="KeyboardE1Slip"/> says by which rule), the compass point from one to the other
    /// (only when adjacent), the distance in key pitches (capped at 3), the time between the slip
    /// and the Backspace, the row band of the slipped key among the rows that hold text keys, and
    /// the slipped key's own arrival and likelihood facts.
    /// </summary>
    public void RecordSlip(bool adjacent, string direction, int distancePitches, int msToBackspace,
        string rowBand, in CorrE1 slipped)
    {
        if (Muted() || !Enabled) return;
        Emit(
            "KB-E1 slip adj={Adj} dir={Dir} dist={Dist} ms={Ms} reg={Reg} saccade={Saccade} restarts={Restarts} " +
            "likely={Likely} start={Start} nocont={NoCont}",
            adjacent, direction, distancePitches, msToBackspace, rowBand, slipped.Saccade, slipped.Restarts,
            slipped.Likely, slipped.WordStart, slipped.NoContinuation);
    }

    /// <summary>
    /// Whether a key class is TEXT: its row, column, action id and label would spell what was typed,
    /// so a line for it carries none of them.
    /// </summary>
    public static bool IsIdentityFree(string keyClass) =>
        keyClass is KeyE1.Letter or KeyE1.Digit or KeyE1.Symbol or KeyE1.Emoji;

    /// <summary>One bar pick. Lengths and ids only; the picked word is never passed in.</summary>
    public void RecordPick(int slot, string source, int prefixLength, int insertedLength,
        int renderToDwellMs, int slotsVisited)
    {
        if (Muted()) return;
        var now = _clockMs();
        var sinceKey = _lastKeyMs == long.MinValue ? -1 : now - _lastKeyMs;
        var sincePick = _lastPickMs == long.MinValue ? -1 : now - _lastPickMs;
        _lastPickMs = now;
        _pickAwaitingUndo = true;
        if (!Enabled) return;
        Emit(
            "KB-E1 pick slot={Slot} src={Source} prefixlen={PrefixLen} inslen={InsLen} sincekey={SinceKeyMs} " +
            "sincepick={SincePickMs} renderms={RenderMs} visited={Visited}",
            slot, source, prefixLength, insertedLength, sinceKey, sincePick, renderToDwellMs, slotsVisited);
    }

    /// <summary>After a Space: was the bar empty?</summary>
    public void RecordSpace(long seq, bool barEmpty)
    {
        if (seq <= 0 || Muted() || !Enabled) return;
        Emit("KB-E1 space seq={Seq} empty={Empty}", seq, barEmpty);
    }

    /// <summary>
    /// A mode change: a sub-layout switch, emoji enter/exit, or a modifier toggle (explicit, not
    /// an auto-return or a one-shot consumed by a key). Detects a mode-undo.
    /// </summary>
    public void RecordMode(string kind, string from, string to)
    {
        if (Muted()) return;
        if (Enabled)
            Emit("KB-E1 mode kind={Kind} from={From} to={To}", kind, from, to);
        NoteModeChange(kind, from, to);
    }

    /// <summary>An edge-bar button (<c>flip</c>/<c>close</c>); a Flip also feeds mode-undo.</summary>
    public void RecordEdge(string button, string edgeAfter, string edgeBefore)
    {
        FlushEdgeTally();   // the tally so far belongs to the edge being left
        if (Enabled)
            Emit("KB-E1 edge button={Button} edge={Edge}", button, edgeAfter);
        if (button == "flip" && !Muted())
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
                Emit("KB-E1 modeundo kind={Kind} ms={Ms}", kind, now - last.AtMs);
            _lastMode = null;   // an undo is not itself undoable
            return;
        }
        _lastMode = new ModeChange(kind, from, to, now);
    }

    /// <summary>
    /// The keyboard was shown. <paramref name="process"/> is the foreground PROCESS name, never a title;
    /// the letter-box size (0 when not yet laid out) is logged here once instead of on every key.
    /// </summary>
    public void RecordShown(string process, string edge, string via, int boxWidthPx = 0, int boxHeightPx = 0)
    {
        _shownAtMs = _clockMs();
        if (!Enabled) return;
        Emit("KB-E1 shown proc={Proc} edge={Edge} via={Via} boxw={BoxW} boxh={BoxH}",
            process, edge, via, boxWidthPx, boxHeightPx);
    }

    /// <summary>The letter-box size after a (re)layout: logged once per layout, never per key.</summary>
    public void RecordLayout(string subLayout, int boxWidthPx, int boxHeightPx)
    {
        if (!Enabled) return;
        Emit("KB-E1 layout sub={Sub} boxw={BoxW} boxh={BoxH}", subLayout, boxWidthPx, boxHeightPx);
    }

    /// <summary>The keyboard was hidden, and why.</summary>
    public void RecordHidden(string cause)
    {
        var now = _clockMs();
        var visibleMs = _shownAtMs == long.MinValue ? -1 : now - _shownAtMs;
        _shownAtMs = long.MinValue;
        _lastMode = null;
        _pickAwaitingUndo = false;
        FlushEdgeTally();   // right BEFORE the hidden line, so a tally never spans two shows
        if (!Enabled) return;
        Emit("KB-E1 hidden cause={Cause} visiblems={VisibleMs}", cause, visibleMs);
    }

    // ---- the per-dock-edge tally (replaces a per-letter edge-quarter class) -------------------------

    private static readonly string[] Quarters = ["-", "N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>One dock edge's counters; they live across shows (in memory only) until written.</summary>
    private sealed class EdgeTally
    {
        public readonly int[] Fires = new int[9];
        public readonly int[] Aborts = new int[9];

        public void Clear()
        {
            Array.Clear(Fires);
            Array.Clear(Aborts);
        }
    }

    private readonly Dictionary<string, EdgeTally> _tallies = new(StringComparer.Ordinal);
    private readonly int _tallyMinFires;

    /// <summary>The minimum this recorder applies (read-only; the production wiring test pins that the real view-model's is the default).</summary>
    internal int EdgeTallyMinFiresInUse => _tallyMinFires;
    private string _tallyEdge = "-";

    /// <summary>
    /// Counts one TEXT key's accepted dwell (or, with <paramref name="abandon"/>, an abandoned one) in the
    /// outer quarter of its box the gaze rested in. Muted keys never get here (callers return first).
    /// A dwell on another dock edge than the last one flushes the last edge's tally first (written only if
    /// it has reached the minimum, otherwise carried).
    /// </summary>
    private void CountEdgeQuarter(string edge, string quarter, bool abandon)
    {
        if (!Enabled) return;
        if (!string.Equals(edge, _tallyEdge, StringComparison.Ordinal))
        {
            FlushEdgeTally();
            _tallyEdge = edge;
        }
        if (!_tallies.TryGetValue(edge, out var tally))
            _tallies[edge] = tally = new EdgeTally();
        var index = Array.IndexOf(Quarters, quarter);
        (abandon ? tally.Aborts : tally.Fires)[index < 0 ? 0 : index]++;
    }

    /// <summary>
    /// Writes the current dock edge's tally IF it has reached <see cref="EdgeTallyMinFires"/> accepted text
    /// dwells, and then resets it; below the minimum the counts are carried over to that edge's next show
    /// (the abandon counters ride along). Called right before the <c>hidden</c> line and, for the edge being
    /// left, right before an edge-button line (a Flip), and when a dwell lands on another edge. The line
    /// carries counts only:
    /// <c>KB-E1 edgeq edge=top fires=F c=.. n=.. ne=.. e=.. se=.. s=.. sw=.. w=.. nw=.. aborts=A ac=.. an=.. …</c>
    /// — <c>fires</c> is the number of accepted text-key dwells on that edge and <c>c</c>…<c>nw</c> split
    /// them by outer quarter (<c>c</c> = the inner half, i.e. no outer quarter); <c>aborts</c> and
    /// <c>ac</c>…<c>anw</c> do the same for text-class abandonments (below the activation).
    /// </summary>
    public void FlushEdgeTally() => FlushOne(_tallyEdge, final: false);

    /// <summary>
    /// The app is shutting down: writes every edge's tally that has reached the minimum, and DROPS the
    /// rest. A leftover below <see cref="EdgeTallyMinFires"/> is never written (a short last show would
    /// otherwise be the one tally that narrows a word). Idempotent: the accumulators are empty afterwards.
    /// </summary>
    public void FlushEdgeTalliesOnShutdown()
    {
        foreach (var edge in _tallies.Keys.OrderBy(e => e, StringComparer.Ordinal).ToList())
            FlushOne(edge, final: true);
    }

    private void FlushOne(string edge, bool final)
    {
        if (!_tallies.TryGetValue(edge, out var tally)) return;
        var fires = tally.Fires.Sum();
        if (fires >= _tallyMinFires)
        {
            if (Enabled)
            {
                var f = tally.Fires;
                var a = tally.Aborts;
                Emit(
                    "KB-E1 edgeq edge={Edge} fires={Fires} c={C} n={N} ne={NE} e={E} se={SE} s={S} sw={SW} w={W} nw={NW} " +
                    "aborts={Aborts} ac={AC} an={AN} ane={ANE} ae={AE} ase={ASE} as={AS} asw={ASW} aw={AW} anw={ANW}",
                    edge, fires, f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8],
                    tally.Aborts.Sum(), a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8]);
            }
            tally.Clear();
        }
        else if (final)
        {
            tally.Clear();   // shutdown drops a leftover below the minimum, never writes it
        }
        // else: carried to this edge's next show
    }

    /// <summary>The mirror check's verdict: an outcome and lengths, never a string.</summary>
    public void RecordMirror(string at, string result, int mirrorLength, int controlLength, int commonSuffix)
    {
        if (!Enabled) return;
        Emit(
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

/// <summary>
/// Everything E1 logs for one key activation. Ints, bools and closed ids only. For a text key the
/// recorder logs only <see cref="EdgeQuarter"/> of the gaze fields (see the recorder's remarks);
/// the pixel offsets, box size, saccade and restarts are kept here for NON-text keys alone.
/// </summary>
public readonly record struct KeyE1(
    long TimestampUs,
    int Row, int Col,
    string Class, string Action,
    int BoxWidthPx, int BoxHeightPx,
    string SubLayout, string Edge,
    int WindowX, int WindowY, int WindowWidth, int WindowHeight,
    long SinceWindowMoveMs,
    int DxPx, int DyPx, int MeanDxPx, int MeanDyPx, string EdgeQuarter,
    int Restarts, string Saccade,
    int EffectiveDwellMs, bool PenaltyApplied,
    bool Typing, string BackspaceClass)
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
    /// <summary>Class: a plain key that types a symbol (punctuation, currency, …): text, so no identity.</summary>
    public const string Symbol = "symbol";
    /// <summary>Class: everything else (Tab, shortcuts, function keys, navigation).</summary>
    public const string Other = "other";
}

/// <summary>
/// What a correction needs about the slipped key: how the gaze arrived (a class of the displacement
/// from the PREVIOUS fire point to this dwell's start, in key pitches), how many dwells were
/// discarded on the way, and the shadow likelihood bits.
/// </summary>
public readonly record struct CorrE1(string Saccade, int Restarts, bool Likely, bool WordStart, bool NoContinuation);

/// <summary>Everything E1 logs for one abandonment.</summary>
public readonly record struct AbandonE1(
    long TimestampUs, int Row, int Col, string Class, string Action,
    int ProgressPct, string Reason, int MeanDxPx, int MeanDyPx, string EdgeQuarter, string SubLayout, string Edge);

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
