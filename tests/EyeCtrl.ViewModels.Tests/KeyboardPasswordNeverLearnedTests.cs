namespace EyeCtrl.ViewModels.Tests;

using System.Globalization;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// A password is never learned. It is typed on the eye keyboard while a sensitive field has focus, and
/// the user then leaves the field by GAZE (a dwell-click on "Log in"): no Tab, Enter or named key reaches
/// the keyboard, so the editing session never ended, and the next Space anywhere learned the password into
/// the PERSISTED user dictionary (and a pair). The detector has no change event, so the keyboard sees the
/// transition sensitive -> not sensitive at the next input and ends the session there (A); and it learns
/// nothing at all while the context is sensitive (B).
/// </summary>
public class KeyboardPasswordNeverLearnedTests
{
    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => [];
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed class Rig : IDisposable
    {
        public KeyboardViewModel Vm { get; }
        public FakeSensitive Sensitive { get; } = new();
        public SimulatedTextTarget Target { get; }
        public PredictionOrchestrator Orchestrator { get; }

        public Rig()
        {
            var messenger = new WeakReferenceMessenger();
            Vm = new KeyboardViewModel(new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } },
                messenger, sensitiveContext: Sensitive);
            Target = new SimulatedTextTarget(messenger);
            Vm.IsVisible = true;
            Orchestrator = new PredictionOrchestrator(new WordPredictor(new InMemoryDictProvider()));
            Vm.SetOrchestrator(Orchestrator);
        }

        public void Type(string text)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) Vm.ActivateKey((string)e.Current, null);
        }

        public IReadOnlyDictionary<string, int> Words => Orchestrator.WordPredictor.WordFrequencies;

        public List<string> Pairs() => Orchestrator.WordPredictor.CaptureLearnedState().Bigrams
            .SelectMany(b => b.Value.Keys.Select(next => b.Key + "->" + next)).ToList();

        /// <summary>The password is typed in a sensitive field, then the focus leaves it by gaze.</summary>
        public void TypePasswordThenLeaveByGaze(string password = "geheim")
        {
            Sensitive.IsSensitiveActive = true;
            Type(password);
            Sensitive.IsSensitiveActive = false;
        }

        public string Mirror => Vm.RecentText + Vm.CurrentInput;

        public void Dispose() => Vm.Dispose();
    }

    private static void AssertNothingLearned(Rig rig, string because)
    {
        Check.WithCustomMessage($"{because}: words learned: " + string.Join(",", rig.Words.Keys)).That(rig.Words).IsEmpty();
        Check.WithCustomMessage($"{because}: pairs learned: " + string.Join(",", rig.Pairs())).That(rig.Pairs()).IsEmpty();
    }

    // ---- A: the session ends on the transition sensitive -> not sensitive ------------------------

    [Fact]
    public void LeavingThePasswordFieldByGaze_ThenSpace_LearnsNothing()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Vm.HandleSpace();

        AssertNothingLearned(rig, "Space after leaving the password field");
        Check.That(rig.Vm.CurrentInput).IsEqualTo("");
        Check.That(rig.Vm.PreviousWord).IsNull();
    }

    [Fact]
    public void LeavingThePasswordField_ThenALetter_StartsAFreshWord()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Type("a");
        Check.WithCustomMessage("the first letter must not extend the password").That(rig.Vm.CurrentInput).IsEqualTo("a");

        rig.Type("b");
        rig.Vm.HandleSpace();

        Check.That(rig.Words.ContainsKey("ab")).IsTrue();
        Check.WithCustomMessage("learned: " + string.Join(",", rig.Words.Keys))
            .That(rig.Words.Keys.Any(k => k.Contains("geheim"))).IsFalse();
        Check.That(rig.Pairs()).IsEmpty();
        Check.That(rig.Mirror).IsEqualTo("ab ");
    }

    [Fact]
    public void LeavingThePasswordField_ThenAPick_NeverDeletesOrExtendsThePassword()
    {
        using var rig = new Rig();
        rig.Target.SeedForeign("");
        rig.TypePasswordThenLeaveByGaze();

        rig.Vm.HandlePredictionSelected("hallo");

        Check.WithCustomMessage("the pick must delete nothing: the password sits in another field")
            .That(rig.Target.LastPickDeletedText).IsEqualTo("");
        Check.WithCustomMessage(string.Join(" | ", rig.Target.Violations)).That(rig.Target.Violations).IsEmpty();
        Check.WithCustomMessage("learned: " + string.Join(",", rig.Words.Keys))
            .That(rig.Words.Keys.Any(k => k.Contains("geheim"))).IsFalse();
        Check.WithCustomMessage("pairs: " + string.Join(",", rig.Pairs())).That(rig.Pairs()).IsEmpty();
        Check.That(rig.Mirror).IsEqualTo("hallo ");
    }

    [Fact]
    public void LeavingThePasswordField_ThenBackspace_DoesNotReopenThePassword()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Vm.HandleBackspace();

        Check.That(rig.Mirror).IsEqualTo("");
    }

    [Fact]
    public void LeavingThePasswordField_ThenWordBackspace_DoesNotTouchThePassword()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Vm.HandleDeleteWord();

        Check.That(rig.Mirror).IsEqualTo("");
    }

    [Fact]
    public void LeavingThePasswordField_ThenAddWord_LearnsNothing()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Vm.HandleAddWord();

        AssertNothingLearned(rig, "Add Word after leaving the password field");
    }

    [Fact]
    public void LeavingThePasswordField_ThenEnter_LeavesNoPassword()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Vm.HandleEnter();
        rig.Vm.HandleSpace();

        AssertNothingLearned(rig, "Enter after leaving the password field");
        Check.That(rig.Mirror).IsEqualTo(" ");
    }

    [Fact]
    public void LeavingThePasswordField_ThenABarRefresh_NoLongerQueriesThePasswordPrefix()
    {
        // The bar's refresh reads CurrentInput without any key: it must not show completions of the
        // password prefix in the next field, and a later pick must not build on it.
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        _ = rig.Vm.GetSuggestionsWithContext();

        Check.That(rig.Vm.CurrentInput).IsEqualTo("");
    }

    // ---- B: nothing is learned while the context is sensitive ------------------------------------

    [Fact]
    public void WhileSensitive_Space_LearnsNothing()
    {
        using var rig = new Rig();
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("geheim");

        rig.Vm.HandleSpace();
        rig.Type("zwei");
        rig.Vm.HandleSpace();

        AssertNothingLearned(rig, "Space in a password field");
    }

    [Fact]
    public void WhileSensitive_AddWord_LearnsNothing()
    {
        using var rig = new Rig();
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("geheim");

        rig.Vm.HandleAddWord();

        AssertNothingLearned(rig, "Add Word in a password field");
    }

    [Fact]
    public void WhileSensitive_APick_LearnsNothing_SingleWordAndMultiWord()
    {
        using var rig = new Rig();
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("geh");

        rig.Vm.HandlePredictionSelected("geheim");
        rig.Vm.HandlePredictionSelected("alpha beta gamma");

        AssertNothingLearned(rig, "a pick in a password field");
    }

    [Fact]
    public void FromOnePasswordFieldToAnother_DoesNotCrash_AndLearnsNothing()
    {
        using var rig = new Rig();
        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("geheim");
        rig.Vm.HandleSpace();

        rig.Sensitive.IsSensitiveActive = true;      // password field -> password field: no transition
        rig.Type("zwei");
        rig.Vm.HandleBackspace();
        rig.Vm.HandleSpace();

        AssertNothingLearned(rig, "password to password");
    }

    // ---- regression: ordinary typing still learns ------------------------------------------------

    [Fact]
    public void ANeverSensitiveWord_StillLearnsOnSpace_AndItsPair()
    {
        using var rig = new Rig();
        rig.Type("ich");
        rig.Vm.HandleSpace();
        rig.Type("habe");
        rig.Vm.HandleSpace();

        Check.That(rig.Words.ContainsKey("ich")).IsTrue();
        Check.That(rig.Words.ContainsKey("habe")).IsTrue();
        Check.That(rig.Pairs()).Contains("ich->habe");
    }

    [Fact]
    public void AWordTypedAfterLeavingThePasswordField_LearnsNormally()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze();

        rig.Type("hallo");
        rig.Vm.HandleSpace();
        rig.Type("welt");
        rig.Vm.HandleSpace();

        Check.That(rig.Words.ContainsKey("hallo")).IsTrue();
        Check.That(rig.Words.ContainsKey("welt")).IsTrue();
        Check.That(rig.Pairs()).Contains("hallo->welt");
        Check.WithCustomMessage("learned: " + string.Join(",", rig.Words.Keys))
            .That(rig.Words.Keys.Any(k => k.Contains("geheim"))).IsFalse();
        Check.WithCustomMessage("pairs: " + string.Join(",", rig.Pairs()))
            .That(rig.Pairs().Any(p => p.Contains("geheim"))).IsFalse();
    }

    [Fact]
    public void ReturningToAPasswordFieldAgain_EndsTheSessionAgainOnTheNextLeave()
    {
        using var rig = new Rig();
        rig.TypePasswordThenLeaveByGaze("eins");
        rig.Type("ok");                               // normal field, a fresh word
        rig.Vm.HandleSpace();

        rig.Sensitive.IsSensitiveActive = true;
        rig.Type("zwei");                             // a second password
        rig.Sensitive.IsSensitiveActive = false;
        rig.Vm.HandleSpace();

        Check.WithCustomMessage("learned: " + string.Join(",", rig.Words.Keys))
            .That(rig.Words.Keys.Any(k => k.Contains("eins") || k.Contains("zwei"))).IsFalse();
        Check.That(rig.Pairs().Any(p => p.Contains("zwei") || p.Contains("eins"))).IsFalse();
    }
}
