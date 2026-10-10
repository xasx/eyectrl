namespace EyeCtrl.App.Diagnostics.SubPixel;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using EyeCtrl.Core.Diagnostics;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using Serilog;

/// <summary>Which motion an interval ran; the JSON discriminator for the three motion records.</summary>
internal enum SubPixelAbMotionKind
{
    /// <summary>Scripted drift.</summary>
    Drift,

    /// <summary>Fractional still park.</summary>
    Static,

    /// <summary>Live target on a straight path.</summary>
    Live,
}

/// <summary>The run-level facts recorded once, before the first block (test design, What is recorded).</summary>
/// <param name="Seed">The schedule seed, so the whole plan can be reproduced.</param>
/// <param name="Ring">The ring settings the probe drew with.</param>
/// <param name="AnswerDwellMs">The answer dwell read from the owner's settings at run time.</param>
/// <param name="DpiScale">Device pixels per DIP.</param>
/// <param name="ScreenBoundsPx">The primary screen, in physical pixels.</param>
/// <param name="DisplayRefreshHz">The display refresh rate, or <c>null</c> when it was not readable.</param>
/// <param name="SampleCadenceMs">The observed sample cadence, or <c>null</c> when it was not measured.</param>
internal sealed record SubPixelAbRunInfo(
    int Seed,
    SubPixelAbRingInfo Ring,
    int AnswerDwellMs,
    double DpiScale,
    PhysicalRect ScreenBoundsPx,
    double? DisplayRefreshHz = null,
    double? SampleCadenceMs = null)
{
    /// <summary>Captures the recorded subset of the owner's cursor settings.</summary>
    /// <param name="settings">The live cursor settings.</param>
    /// <returns>The snapshot the results file holds.</returns>
    public static SubPixelAbRingInfo CaptureRing(CursorSettings settings) => new(
        settings.RingStyle.ToString(),
        settings.CircleSize,
        settings.CircleThickness,
        settings.CircleOpacity,
        settings.CircleColor);
}

/// <summary>The ring look recorded with a run.</summary>
/// <param name="Style">The ring stack style name.</param>
/// <param name="SizeDip">The ring diameter, DIPs.</param>
/// <param name="Thickness">The core band thickness.</param>
/// <param name="Opacity">The configured ring opacity.</param>
/// <param name="Color">The configured core colour, hex.</param>
internal sealed record SubPixelAbRingInfo(
    string Style, double SizeDip, double Thickness, double Opacity, string Color);

/// <summary>One presented trial, mapped from <see cref="SubPixelProbeTrialOutcome"/> for the file.</summary>
internal sealed record SubPixelAbTrialResult(
    int IndexInBlock,
    SubPixelAbOrder Order,
    SubPixelAbMotionKind MotionKind,
    double? StartX,
    double? StartY,
    SubPixelAbDirection? Direction,
    double? TargetX,
    double? TargetY,
    double? TargetDirectionX,
    double? TargetDirectionY,
    SubPixelAbCondition? Answer,
    SubPixelProbeVoidReason? VoidReason,
    long ResponseTimeMs,
    long LongestInvalidGazeMs,
    long LongestRenderGapMs);

/// <summary>One finished block with its tally and every presentation in order.</summary>
internal sealed record SubPixelAbBlockResult(
    SubPixelAbBlock Block,
    int APicks,
    int BPicks,
    int Voids,
    int Scored,
    IReadOnlyList<SubPixelAbTrialResult> Trials);

/// <summary>One finished session with its verdict text, written before Ask/Result.</summary>
internal sealed record SubPixelAbSessionResult(
    SubPixelProbeSessionKind Session,
    SubPixelProbeEndReason Reason,
    bool Aborted,
    SubPixelAbOutcome? Outcome,
    SubPixelAbStaticReadout? StaticReadout,
    string? StaticPicksOfAAndScored,
    string? LogLine);

/// <summary>The whole results document.</summary>
internal sealed record SubPixelAbDocument(
    int SchemaVersion,
    string Kind,
    DateTimeOffset StartedUtc,
    string AppVersion,
    int Seed,
    double DpiScale,
    double ScreenWidthPx,
    double ScreenHeightPx,
    double? DisplayRefreshHz,
    double? SampleCadenceMs,
    int AnswerDwellMs,
    SubPixelAbRingInfo Ring,
    SubPixelProbeStartMode StartMode,
    string? ResumedSession1LogLine,
    IReadOnlyList<SubPixelAbBlockResult> Blocks,
    IReadOnlyList<SubPixelAbSessionResult> Sessions,
    SubPixelProbeRunEndReason? RunEndReason);

/// <summary>
/// C11 — the sub-pixel probe's results writer. Writes an atomic JSON document to
/// <c>%APPDATA%\EyeCtrl\subpixel-ab-&lt;UTC&gt;.json</c> after every block and every session, so an
/// abort never loses a completed result (test design, What is recorded).
///
/// <para>
/// The file is rewritten whole on every event (temp file + <c>File.Move(overwrite: true)</c>, the
/// repo's <c>ScriptedGroundTruthRecorder</c> pattern), so a reader never sees a partial document.
/// C7 drops events once the run is aborted before they reach this writer; a run that never started
/// a session still writes its run-level facts when it ends.
/// </para>
/// </summary>
internal sealed class SubPixelAbResultWriter
{
    /// <summary>The document schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly SubPixelAbRunInfo _info;
    private readonly string _filePath;
    private readonly List<SubPixelAbBlockResult> _blocks = new();
    private readonly List<SubPixelAbSessionResult> _sessions = new();
    private readonly SubPixelProbeStartMode _startMode;
    private readonly string? _resumedSession1LogLine;
    private SubPixelProbeRunEndReason? _runEndReason;

    /// <summary>Creates a writer for one run. Nothing is written until the first event.</summary>
    /// <param name="filePath">The full path of the result file.</param>
    /// <param name="info">The run-level facts.</param>
    /// <param name="startMode">The run's start mode (full run or session-2 resume).</param>
    /// <param name="resumedSession1LogLine">
    /// The persisted session-1 log line a resume continues from, or <c>null</c>.
    /// </param>
    public SubPixelAbResultWriter(
        string filePath,
        SubPixelAbRunInfo info,
        SubPixelProbeStartMode startMode = SubPixelProbeStartMode.FullRun,
        string? resumedSession1LogLine = null)
    {
        _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        _startMode = startMode;
        _resumedSession1LogLine = resumedSession1LogLine;
    }

    /// <summary>The file this writer owns, for the log and tests.</summary>
    public string FilePath => _filePath;

    /// <summary>Appends a finished block and rewrites the file.</summary>
    /// <param name="outcome">The block that finished.</param>
    public void WriteBlock(SubPixelProbeBlockOutcome outcome)
    {
        _blocks.Add(new SubPixelAbBlockResult(
            outcome.Block,
            outcome.Tally.APicks,
            outcome.Tally.BPicks,
            outcome.Tally.Voids,
            outcome.Tally.Scored,
            [.. outcome.Trials.Select(MapTrial)]));
        Save();
    }

    /// <summary>
    /// Records a finished session — its verdicts, its static readout and its log line — and
    /// rewrites the file. Called before Ask/Result so an abort there never loses the result.
    /// </summary>
    /// <param name="outcome">The session that ended.</param>
    /// <param name="session1">Session 1's verdict, when this was session 1.</param>
    /// <param name="session2">Session 2's verdict, when this was session 2.</param>
    public void WriteSession(
        SubPixelProbeSessionOutcome outcome,
        SubPixelAbSession1Verdict? session1,
        SubPixelAbSession2Verdict? session2)
    {
        string? staticPicks = session1 is { StaticReadout: not SubPixelAbStaticReadout.Skipped } s1
            ? $"{s1.StaticReadout}"
            : null;

        _sessions.Add(new SubPixelAbSessionResult(
            outcome.Session,
            outcome.Reason,
            outcome.Aborted,
            session1?.Outcome ?? session2?.Outcome,
            session1?.StaticReadout,
            staticPicks,
            session1?.LogLine ?? session2?.LogLine));

        if (session1 is not null)
            Log.Information("{LogLine}", session1.LogLine);
        else if (session2 is not null)
            Log.Information("{LogLine}", session2.LogLine);

        Save();
    }

    /// <summary>Records why the run ended and rewrites the file.</summary>
    /// <param name="reason">The run's end reason.</param>
    public void WriteRunEnd(SubPixelProbeRunEndReason reason)
    {
        _runEndReason = reason;
        Save();
    }

    private static SubPixelAbTrialResult MapTrial(SubPixelProbeTrialOutcome outcome)
    {
        var trial = outcome.Trial;

        return trial.Motion switch
        {
            SubPixelAbDriftMotion drift => new SubPixelAbTrialResult(
                trial.IndexInBlock, trial.Order, SubPixelAbMotionKind.Drift,
                drift.Start.X, drift.Start.Y, drift.Direction,
                null, null, null, null,
                outcome.Answer, outcome.VoidReason, outcome.ResponseTimeMs,
                outcome.LongestInvalidGazeMs, outcome.LongestRenderGapMs),

            SubPixelAbStaticMotion park => new SubPixelAbTrialResult(
                trial.IndexInBlock, trial.Order, SubPixelAbMotionKind.Static,
                park.Position.X, park.Position.Y, null,
                null, null, null, null,
                outcome.Answer, outcome.VoidReason, outcome.ResponseTimeMs,
                outcome.LongestInvalidGazeMs, outcome.LongestRenderGapMs),

            SubPixelAbLiveMotion live => new SubPixelAbTrialResult(
                trial.IndexInBlock, trial.Order, SubPixelAbMotionKind.Live,
                null, null, null,
                live.TargetStart.X, live.TargetStart.Y, live.DirectionX, live.DirectionY,
                outcome.Answer, outcome.VoidReason, outcome.ResponseTimeMs,
                outcome.LongestInvalidGazeMs, outcome.LongestRenderGapMs),

            _ => throw new ArgumentOutOfRangeException(nameof(outcome), trial.Motion, "Unknown motion."),
        };
    }

    private void Save()
    {
        var document = new SubPixelAbDocument(
            CurrentSchemaVersion,
            "subpixel-ab",
            DateTimeOffset.UtcNow,
            typeof(SubPixelAbResultWriter).Assembly.GetName().Version?.ToString() ?? "unknown",
            _info.Seed,
            _info.DpiScale,
            _info.ScreenBoundsPx.Width,
            _info.ScreenBoundsPx.Height,
            _info.DisplayRefreshHz,
            _info.SampleCadenceMs,
            _info.AnswerDwellMs,
            _info.Ring,
            _startMode,
            _resumedSession1LogLine,
            [.. _blocks],
            [.. _sessions],
            _runEndReason);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var temporary = _filePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporary, _filePath, overwrite: true);
    }
}
