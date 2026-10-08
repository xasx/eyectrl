namespace EyeCtrl.App.Tests.Settings.Pages;

using System.Linq;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Enums;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;
using NFluent;
using Xunit;

/// <summary>
/// T04's Settings language row: first on General, endonym choices through T01's token mapping,
/// saved at once, and a restart prompt in the TARGET language. The prompt is raised only when the
/// choice resolves to a language other than the one running — a no-op choice has nothing to
/// restart for (and shows no pending note).
/// </summary>
public class GeneralPageSourceLanguageTests
{
    private static LanguageTestSupport.Fixture Build() => LanguageTestSupport.Build();

    [Fact]
    public void TheLanguageRow_IsFirst_WithEndonymsForDisplayAndJsonTokensForCodes()
    {
        var fixture = Build();
        var rows = fixture.Rows();

        var language = rows[0] as ComboBoxSpec;
        Check.That(language).IsNotNull();
        Check.That(language!.Id).IsEqualTo("g_language");
        Check.That(language.Display).ContainsExactly("System", "Deutsch", "English");
        Check.That(language.Code).ContainsExactly("system", "german", "english");
        Check.That(language.Get()).IsEqualTo("system");
        Check.That(language.Description).Not.IsEmpty();
    }

    [Fact]
    public void ChoosingALanguage_SavesAtOnce_AndOpensTheTargetLanguagePrompt()
    {
        var fixture = Build();

        fixture.LanguageRow().Set("german");

        Check.That(fixture.Settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
        Check.That(fixture.Saves).HasSize(1);
        Check.That(fixture.Rebuilds).HasSize(1);
        Check.That(fixture.ViewModel.IsModalVisible).IsTrue();
        Check.That(fixture.ViewModel.ModalMessage).IsEqualTo("The new language takes effect after a restart.");
        Check.That(fixture.ViewModel.ModalChoices.Select(c => c.Id))
            .ContainsExactly("language_restart", "language_cancel");
        Check.That(fixture.ViewModel.ModalChoices[0].Label).IsEqualTo("Restart now");
        Check.That(fixture.ViewModel.ModalChoices[0].Kind).IsEqualTo(ModalChoiceKind.Affirmative);
        Check.That(fixture.ViewModel.ModalChoices[1].Label).IsEqualTo("Cancel");
        Check.That(fixture.ViewModel.ModalChoices[1].Kind).IsEqualTo(ModalChoiceKind.Negative);
        Check.That(fixture.ViewModel.ModalChoices[1].OnChosen).IsNull();
    }

    [Fact]
    public void TheRestartChoice_SendsRestartAppRequested()
    {
        var fixture = Build();
        RestartAppRequestedMessage? requested = null;
        fixture.Messenger.Register<RestartAppRequestedMessage>(
            this, (_, m) => requested = m);

        fixture.LanguageRow().Set("german");
        fixture.ViewModel.ModalChoices[0].OnChosen!.Invoke();

        Check.That(requested).IsNotNull();
    }

    [Fact]
    public void Cancelling_KeepsTheChoiceSaved_AndThePendingNoteVisible()
    {
        var fixture = Build();

        fixture.LanguageRow().Set("german");
        // The negative choice has no callback; the window hides the modal. Nothing else runs.
        fixture.ViewModel.HideModal();

        Check.That(fixture.Settings.General.UiLanguage).IsEqualTo(UiLanguage.German);
        Check.That(fixture.Rows().OfType<LabelSpec>().Any(
                l => l.Text() == "Wird nach Neustart wirksam / Takes effect after restart"))
            .IsTrue();
    }

    [Fact]
    public void ChoosingTheLanguageAlreadyRunning_SavesButDoesNotPrompt_AndShowsNoNote()
    {
        // Default selection is System; the embedded English table runs English, so picking
        // "system" resolves to the running language. Nothing to restart for.
        var fixture = Build();

        fixture.LanguageRow().Set("system");

        Check.That(fixture.Saves).HasSize(1);          // the choice is still persisted at once
        Check.That(fixture.ViewModel.IsModalVisible).IsFalse();
        Check.That(fixture.Rows().OfType<LabelSpec>().Any(
            l => l.Text().Contains("Takes effect after restart"))).IsFalse();
    }

    [Fact]
    public void ThePendingNote_AppearsWhenTheSavedChoiceResolvesElsewhere_AndClearsWhenItMatchesAgain()
    {
        var fixture = Build();

        Check.That(fixture.Rows().OfType<LabelSpec>().Any(
            l => l.Text().Contains("Takes effect after restart"))).IsFalse();

        fixture.LanguageRow().Set("german");
        Check.That(fixture.Rows().OfType<LabelSpec>().Any(
                l => l.Text() == "Wird nach Neustart wirksam / Takes effect after restart"))
            .IsTrue();

        // Back to the running language: the note clears (and there is no prompt).
        fixture.ViewModel.HideModal();
        fixture.LanguageRow().Set("english");
        Check.That(fixture.Rows().OfType<LabelSpec>().Any(
            l => l.Text().Contains("Takes effect after restart"))).IsFalse();
        Check.That(fixture.ViewModel.IsModalVisible).IsFalse();
    }

    [Fact]
    public void AnUnknownCode_IsIgnored()
    {
        var fixture = Build();

        fixture.LanguageRow().Set("klingon");

        Check.That(fixture.Settings.General.UiLanguage).IsEqualTo(UiLanguage.System);
        Check.That(fixture.Saves).IsEmpty();
        Check.That(fixture.ViewModel.IsModalVisible).IsFalse();
    }
}
