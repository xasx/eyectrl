namespace EyeCtrl.App.Services;

using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using EyeCtrl.Setup;

/// <summary>
/// The two setup-last facts the one-time notice needs, behind a seam: the record Setup wrote and
/// the time of the notice already shown. Fake in the tests; the real files otherwise.
/// </summary>
public interface ISetupNoticeStore
{
    /// <summary><c>%APPDATA%\EyeCtrl\setup-last.json</c>, or null when absent/unreadable.</summary>
    SetupResult? TryReadRecord();

    /// <summary>The marker's path (the file watcher subscribes to its folder). Empty for a fake.</summary>
    string RecordPath { get; }

    /// <summary>The recorded time of the last shown notice, or null when none was shown.</summary>
    DateTimeOffset? ReadShownTime();

    /// <summary>Records that the notice for this record time was shown. Never throws — a
    /// marker that cannot be written may cost a repeated notice, nothing more.</summary>
    void WriteShownTime(DateTimeOffset time);
}

/// <summary>
/// The production marker reader/writer. Per the coordinator's ruling, <c>setup-last.json</c> is
/// Setup's diagnostic record and is never deleted or edited here; the "shown" fact lives in the
/// tiny <c>setup-notice-shown.json</c> beside it.
/// </summary>
public sealed class SetupNoticeFiles : ISetupNoticeStore
{
    /// <summary>The marker file's name under <c>%APPDATA%\EyeCtrl</c>.</summary>
    public const string MarkerName = "setup-notice-shown.json";

    private readonly string _recordPath;
    private readonly string _markerPath;

    /// <summary>Creates the store over two explicit paths (tests use temp files or a fake).</summary>
    public SetupNoticeFiles(string recordPath, string markerPath)
    {
        _recordPath = recordPath ?? throw new ArgumentNullException(nameof(recordPath));
        _markerPath = markerPath ?? throw new ArgumentNullException(nameof(markerPath));
    }

    /// <summary>The record's path — the file watcher listens in its folder.</summary>
    public string RecordPath => _recordPath;

    /// <summary>The production layout: <c>SetupPaths</c>'s record path plus the marker beside it.</summary>
    public static SetupNoticeFiles CreateForProduction()
    {
        var paths = new SetupPaths(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        return new SetupNoticeFiles(paths.RecordPath, Path.Combine(paths.AppDataRoot, MarkerName));
    }

    /// <inheritdoc />
    public SetupResult? TryReadRecord()
    {
        try
        {
            if (!File.Exists(_recordPath))
                return null;

            return JsonSerializer.Deserialize<SetupResult>(File.ReadAllText(_recordPath), SetupJson.Options);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public DateTimeOffset? ReadShownTime()
    {
        try
        {
            if (!File.Exists(_markerPath))
                return null;

            using var document = JsonDocument.Parse(File.ReadAllText(_markerPath));
            return document.RootElement.TryGetProperty("shownTime", out var value)
                   && value.ValueKind == JsonValueKind.String
                   && DateTimeOffset.TryParse(
                       value.GetString(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public void WriteShownTime(DateTimeOffset time)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
            File.WriteAllText(
                _markerPath,
                JsonSerializer.Serialize(new { shownTime = time }, SetupJson.Options));
        }
        catch (Exception)
        {
            // Same contract as Setup's own result recorder: losing the marker costs one repeated
            // notice later, never a broken start.
        }
    }
}

/// <summary>
/// The S6f startup notice: show the last setup outcome ONCE, when it was a rollback, a failure or
/// the Q12 <c>degraded</c> outcome. The shown fact is keyed by the record's <c>time</c> — a newer
/// record shows again, the same record never does (the coordinator's ruling: do not touch
/// setup-last.json; record the shown time in <c>setup-notice-shown.json</c>).
/// </summary>
public sealed class SetupLastNoticeService
{
    private readonly ISetupNoticeStore _store;
    private readonly Func<string, object?[], string> _resolve;
    private readonly Func<string> _closeLabel;
    private readonly Action<string, string> _show;

    /// <summary>Creates the service.</summary>
    /// <param name="store">The record and marker seam.</param>
    /// <param name="resolve">Resolves a notice key in the notice language (the saved selection
    /// resolved against the machine culture — what the running build speaks).</param>
    /// <param name="closeLabel">The card's single acknowledgement label.</param>
    /// <param name="show">Presents the card (text, close label); the production card is the
    /// existing restart-notice window, a fake in the tests.</param>
    public SetupLastNoticeService(
        ISetupNoticeStore store,
        Func<string, object?[], string> resolve,
        Func<string> closeLabel,
        Action<string, string> show)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _closeLabel = closeLabel ?? throw new ArgumentNullException(nameof(closeLabel));
        _show = show ?? throw new ArgumentNullException(nameof(show));
    }

    /// <summary>
    /// Shows the notice when this record was not shown yet. Returns whether it showed. Marking
    /// happens BEFORE presenting: a crash while the card is up must not loop the notice forever.
    /// </summary>
    public bool CheckStartup()
    {
        SetupResult? record;
        try
        {
            record = _store.TryReadRecord();
        }
        catch (Exception)
        {
            return false;
        }

        if (record is null)
            return false;

        var notice = NoticeFor(record);
        if (notice is null)
            return false;

        var shown = _store.ReadShownTime();
        // S1 (review 03): WriteShownTime is best-effort — a marker that could not be persisted must
        // not let the watch re-show the same card on every poll. The in-memory fact counts as seen
        // too, so the effective marker is the later of the two.
        var lastSeen = shown is { } persisted
            ? (LastShownTime is { } inMemory && inMemory > persisted ? inMemory : persisted)
            : LastShownTime;
        if (lastSeen is { } seenTime && record.Time <= seenTime)
            return false;

        _store.WriteShownTime(record.Time);
        LastShownTime = record.Time;
        _show(_resolve(notice.Value.Key, notice.Value.Args), _closeLabel());
        return true;
    }

    /// <summary>
    /// The record time of the last notice this service showed, or null when it never showed one.
    /// The startup watch uses it to tell a PREVIOUS session's record (shown on the immediate check,
    /// which must not end the watch) from this session's own late record (review 02 R5).
    /// </summary>
    public DateTimeOffset? LastShownTime { get; private set; }

    /// <summary>
    /// Which outcomes earn a notice and the key/args each speaks (review 01 M3 + N1).
    /// <list type="bullet">
    /// <item><c>RolledBack</c> names the RESTORED version — Setup writes
    /// <c>Finish(verb, outcome, attempted, restored, …)</c>, so <c>Version</c> is the failed one and
    /// <c>PreviousVersion</c> the one now running.</item>
    /// <item><c>Degraded</c> names the running version (Q12).</item>
    /// <item>The failed family routes by verb: an update failure speaks the update sentence (a
    /// version-less one when <c>Version</c> is null, as every refused record has), a rollback
    /// failure its own, and an uninstall failure stays silent — S6e's window shows it while the
    /// App keeps running, so the next start must not announce a failed "update" that never happened.
    /// Repair/Status failures stay silent too.</item>
    /// </list>
    /// Deliberate no-notice set besides those: Ok/Uninstalled/Repaired (the result screen just
    /// showed them), HandedOff (transient; the terminal record follows), Cancelled (the owner chose
    /// it in Setup's own window) and Blocked (Setup's window explains the policy to the caregiver).
    /// </summary>
    internal static (string Key, object?[] Args)? NoticeFor(SetupResult record) =>
        record.Outcome switch
        {
            SetupOutcome.RolledBack => record.PreviousVersion is { } restored
                ? ("update.notice.rolledBack", [restored])
                : ("update.notice.failed.noVersion", Array.Empty<object?>()),
            SetupOutcome.Degraded => record.Version is { } running
                ? ("update.notice.degraded", [running])
                : ("update.notice.failed.noVersion", Array.Empty<object?>()),
            SetupOutcome.AllFailed or SetupOutcome.VerifyFailed
                or SetupOutcome.NotApplied or SetupOutcome.Failed
                => record.Verb switch
                {
                    SetupVerb.Update => record.Version is { } attempted
                        ? ("update.notice.failed", [attempted])
                        : ("update.notice.failed.noVersion", Array.Empty<object?>()),
                    SetupVerb.Rollback => ("rollback.notice.failed", Array.Empty<object?>()),
                    _ => null,
                },
            _ => null,
        };
}
