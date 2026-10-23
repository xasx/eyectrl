namespace EyeCtrl.App.Tests.Localization.Fit;

using System;
using System.Linq;
using System.Windows;
using EyeCtrl.App.Views;
using EyeCtrl.Core.Localization;
using EyeCtrl.Tests.TestSupport;
using EyeCtrl.ViewModels;

/// <summary>
/// A2 review 02 N1, the other side of the release radius: a choice held under a resting gaze must
/// still arm when the owner simply READS the next question. So the question text above the buttons
/// has to lie outside every button's release circle (ModalBtnRadius + the latch's release margin),
/// measured on the real modal layout at 200 %, in English and in German. The reading point is the
/// middle of the message's LAST line — the line nearest the buttons.
/// </summary>
[Collection("WpfApplication")]
public class SafetySetupReadAwayGeometryTests
{
    private readonly ITestOutputHelper _output;

    public SafetySetupReadAwayGeometryTests(ITestOutputHelper output) => _output = output;

    public static TheoryData<string, string, string> Questions()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var table in new[] { "english", "german" })
        {
            foreach (var key in new[] { "settings.safety.crash.message", "settings.safety.boot.message",
                         "settings.safety.cue.message", "settings.safety.track.message" })
                data.Add(table, key, "settings.modal.yes|settings.modal.no");
            data.Add(table, "settings.safety.mode.message", "settings.safety.mode.basic|settings.safety.mode.all");
        }

        return data;
    }

    [StaTheory]
    [MemberData(nameof(Questions))]
    public void ReadingTheQuestion_LeavesEveryButtonsReleaseCircle(string table, string messageKey, string choiceKeys)
    {
        var strings = table == "german" ? GermanDraft.Table : StringTable.EmbeddedEnglish;
        var choices = choiceKeys.Split('|').Select(k => strings.T(k)).ToList();
        var layout = SettingsModalExceptionTests.Layout(strings.T(messageKey, 2, 5), choices);
        var oneLine = SettingsModalExceptionTests.Layout("Xy", choices).MessageBox.Height;

        var reading = new Point(
            layout.MessageBox.X + layout.MessageBox.Width / 2,
            layout.MessageBox.Bottom - oneLine / 2);
        var release = SettingsWindow.ModalBtnRadius + SettingsWindowViewModel.LatchReleaseMarginDip;

        foreach (var button in layout.Buttons)
        {
            var centre = new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
            var distance = (reading - centre).Length;
            _output.WriteLine($"{table} {messageKey}: reading point {distance:0.#} DIP from a button centre (release {release})");
            Check.WithCustomMessage(
                    $"{table} {messageKey}: reading the question's last line is {distance:0.#} DIP from a button "
                    + $"centre, inside its {release} DIP release circle — a held choice would never arm by reading.")
                .That(distance).IsStrictlyGreaterThan(release);
        }
    }
}
