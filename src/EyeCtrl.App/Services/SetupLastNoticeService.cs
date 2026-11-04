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
        if (shown is { } shownTime && record.Time <= shownTime)
            return false;

        _store.WriteShownTime(record.Time);
        _show(_resolve(notice.Value.Key, notice.Value.Args), _closeLabel());
        return true;
    }

    /// <summary>
    /// Which outcomes earn a notice and the key/args each speaks. Rolled-back and the Q12
    /// <c>degraded</c> have their own words (the ticket); the failed family shares one sentence.
    /// Deliberate no-notice set: Ok/Uninstalled/Repaired (the result screen just showed them),
    /// HandedOff (transient; the terminal record follows), Cancelled (the owner chose it in
    /// Setup's own window) and Blocked (Setup's window explains the policy to the caregiver).
    /// </summary>
    internal static (string Key, object?[] Args)? NoticeFor(SetupResult record) =>
        record.Outcome switch
        {
            SetupOutcome.RolledBack => ("update.notice.rolledBack", [record.Version]),
            SetupOutcome.Degraded => ("update.notice.degraded", [record.Version]),
            SetupOutcome.AllFailed or SetupOutcome.VerifyFailed
                or SetupOutcome.NotApplied or SetupOutcome.Failed
                => ("update.notice.failed", [record.Version]),
            _ => null,
        };
}
