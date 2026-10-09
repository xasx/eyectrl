namespace EyeCtrl.Supervisor.Tests.Supervision;

using System.Globalization;
using EyeCtrl.Core.Enums;
using EyeCtrl.Core.Localization;
using EyeCtrl.Core.Models;
using EyeCtrl.Supervisor.Ipc;
using EyeCtrl.Supervisor.Supervision;

/// <summary>
/// What the setup window's language row shows. The texts are crisis-surface strings, so they are
/// asserted as English literals, never through <c>T(key)</c> — except the one German case, which
/// builds its own two-language table to prove the note follows the CHOSEN language.
/// </summary>
public class LanguageSetupRowTests
{
    private readonly List<TaskCompletionSource<AppRequestResult>> _replies = new();
    private UiLanguage? _disk = UiLanguage.English;
    private DateTime _now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private readonly SettingDelivery<UiLanguage> _delivery;
    private readonly StringTable _english = StringTable.EmbeddedEnglish;

    public LanguageSetupRowTests()
    {
        _delivery = new SettingDelivery<UiLanguage>(
            "UiLanguage",
            (language, restart) => SettingRequest.SetUiLanguage(language, restart),
            _ =>
            {
                var reply = new TaskCompletionSource<AppRequestResult>();
                _replies.Add(reply);
                return reply.Task;
            },
            work => work(),
            readDisk: () => _disk,
            runInBackground: work => work(),
            utcNow: () => _now,
            confirmSaveByReRead: true);
    }

    private LanguageSetupRow Open(UiLanguage onDisk = UiLanguage.English) =>
        new(_delivery, () => onDisk, CultureInfo.GetCultureInfo("de-DE"));

    private static AppRequestResult NotAvailable => AppRequestResult.NotAvailable("test: no App");

    [Fact]
    public void AtRest_TheDiskValueIsSelected_AndMarked()
    {
        using var row = Open(UiLanguage.System);

        Check.That(row.Selected).IsEqualTo(UiLanguage.System);
        Check.That(LanguageSetupRow.Choices.Select(row.Caption))
            .ContainsExactly("✓ System", "Deutsch", "English");
        Check.That(row.Note(_english)).IsNull();
        Check.That(LanguageSetupRow.Label(_english)).IsEqualTo("Sprache / Language");
    }

    [Fact]
    public void ALiveChoice_IsMarkedAtOnce_AndSaysItIsAsking()
    {
        using var row = Open();
        row.Choose(UiLanguage.German);

        Check.That(row.Selected).IsEqualTo(UiLanguage.German);
        Check.That(row.Caption(UiLanguage.German)).IsEqualTo("✓ Deutsch");
        Check.That(row.Caption(UiLanguage.English)).IsEqualTo("English");
        Check.That(row.StatusText(_english)).IsEqualTo("Asking EyeCtrl to change the language…");
        Check.That(row.Note(_english)).IsNull();
    }

    [Fact]
    public void AChoiceTheAppCannotTake_IsPending_WithTheHonestNote_AndTheWaitingStatus()
    {
        using var row = Open();
        row.Choose(UiLanguage.English);           // English chosen: the note speaks English
        _replies[0].SetResult(NotAvailable);

        Check.That(row.IsPending).IsTrue();
        Check.That(row.Selected).IsEqualTo(UiLanguage.English);
        Check.That(row.Note(_english)).IsEqualTo(
            "Not saved yet. Applies when EyeCtrl next starts, unless the PC or the Supervisor restarts first.");
        Check.That(row.StatusText(_english)).IsEqualTo("The language is waiting for EyeCtrl.");
    }

    [Fact]
    public void ThePendingNote_SpeaksTheChosenLanguage_NotTheWindows()
    {
        // A two-language table running English with the language-recovery surface in German: the
        // note for a pending German choice comes out German although the table runs English.
        var english = StringTableContent.ParseEnglish("""
            {"language-recovery.supervisor.pending": {"text": "Applies when EyeCtrl next starts", "who": "caretaker",
              "where": "language-recovery", "fit": "prose", "args": [], "crisis": true}}
            """);
        var german = StringTableContent.ParseGerman("""
            {"language-recovery.supervisor.pending": {"text": "Gilt beim nächsten Start von EyeCtrl", "src": "00000000"}}
            """);
        var table = StringTable.Create(
            UiLanguage.English, CultureInfo.GetCultureInfo("de-DE"), english, german, ["language-recovery"]);
        using var row = Open();
        row.Choose(UiLanguage.German);
        _replies[0].SetResult(NotAvailable);

        Check.That(table.Language).IsEqualTo(UiLanguage.English);
        Check.That(row.Note(table)).IsEqualTo("Gilt beim nächsten Start von EyeCtrl");
    }

    [Fact]
    public void ThePendingNote_ResolvesSystemAgainstWindows_BeforeChoosingItsLanguage()
    {
        // System on a de-DE machine is German, so the note for a pending System is German too —
        // not "this table's language" (English), which the lookup's own System would mean.
        var english = StringTableContent.ParseEnglish("""
            {"language-recovery.supervisor.pending": {"text": "Applies when EyeCtrl next starts", "who": "caretaker",
              "where": "language-recovery", "fit": "prose", "args": [], "crisis": true}}
            """);
        var german = StringTableContent.ParseGerman("""
            {"language-recovery.supervisor.pending": {"text": "Gilt beim nächsten Start von EyeCtrl", "src": "00000000"}}
            """);
        var table = StringTable.Create(
            UiLanguage.English, CultureInfo.GetCultureInfo("de-DE"), english, german, ["language-recovery"]);
        using var row = Open();
        row.Choose(UiLanguage.System);
        _replies[0].SetResult(NotAvailable);

        Check.That(row.Note(table)).IsEqualTo("Gilt beim nächsten Start von EyeCtrl");
    }

    [Fact]
    public void ASavedChoice_SaysSaved_AndStaysMarked()
    {
        using var row = Open();
        row.Choose(UiLanguage.German);
        _disk = UiLanguage.German;
        _replies[0].SetResult(new AppRequestResult(SettingRequestStatus.Saved, "test"));

        Check.That(row.IsPending).IsFalse();
        Check.That(row.Selected).IsEqualTo(UiLanguage.German);
        Check.That(row.StatusText(_english)).IsEqualTo("Language saved.");
    }

    [Fact]
    public void ARestartAlreadyRunning_IsSaidSo()
    {
        using var row = Open();
        row.Choose(UiLanguage.German);
        _disk = UiLanguage.German;
        _replies[0].SetResult(new AppRequestResult(SettingRequestStatus.RestartInProgress, "test"));

        Check.That(row.StatusText(_english)).IsEqualTo("Language saved. EyeCtrl is already restarting.");
    }

    [Fact]
    public void ARefusedChoice_SaysSo_AndTheDiskValueIsMarkedAgain()
    {
        using var row = Open(UiLanguage.English);
        row.Choose(UiLanguage.German);
        _replies[0].SetResult(new AppRequestResult(SettingRequestStatus.Rejected, "test"));

        Check.That(row.Selected).IsEqualTo(UiLanguage.English);
        Check.That(row.StatusText(_english)).IsEqualTo("EyeCtrl refused the language. It was not saved.");
    }

    [Fact]
    public void AChoiceOvertakenInTheApp_IsDropped_AndTheNewerValueIsMarked()
    {
        using var row = Open(UiLanguage.English);
        row.Choose(UiLanguage.German);
        _replies[0].SetResult(NotAvailable);      // parks with the disk at English

        _disk = UiLanguage.System;                // the user picked System in EyeCtrl since
        _now = _delivery.NextRetryAt!.Value;
        _delivery.OnRetryTick();

        Check.That(_replies).HasSize(1);          // the stale German is never sent
        Check.That(row.IsPending).IsFalse();
        Check.That(row.Selected).IsEqualTo(UiLanguage.System);
        Check.That(row.StatusText(_english))
            .IsEqualTo("The language was changed in EyeCtrl meanwhile. That newer choice stays.");
    }
}
