namespace EyeCtrl.ViewModels.Tests;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Interfaces;
using EyeCtrl.Core.Keyboard;
using EyeCtrl.Core.Models;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;
using EyeCtrl.ViewModels.Messages;

/// <summary>
/// D3: password fields FAIL CLOSED. The direct IsPassword poll decides — at keyboard show and at the start
/// and commit of every typed word — and the UIA event path can only raise the sensitive state, never clear
/// what the poll found. A poll that cannot answer (timeout, busy, error) is UNKNOWN and fails closed for
/// learning (all three learn paths), the suggestion bar and the LM prompt; typing itself is never touched.
/// Everything here is headless: fakes only, no UIA read, no window.
/// </summary>
public class KeyboardPollDecidesTests
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private sealed class FakeSensitive : ISensitiveContextDetector
    {
        public bool IsSensitiveActive { get; set; }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakePoll : IFocusedPasswordPoll
    {
        public Func<bool?> Read = () => false;
        public int Reads;

        public bool? ReadIsPassword()
        {
            Interlocked.Increment(ref Reads);
            return Read();
        }
    }

    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => new() { ["hallo"] = 100, ["halle"] = 90, ["haus"] = 80 };
        public void Save(Dictionary<string, int> words) { }
    }

    private sealed class FakeLm : ILanguageModelPredictor
    {
        public List<string> NextWordCalls { get; } = [];
        public List<(string Context, string? Prefix)> CompletionCalls { get; } = [];
        public bool IsAvailable => true;
        public void Predict(string context, string? prefix = null) => CompletionCalls.Add((context, prefix));
        public void PredictNextWord(string context) => NextWordCalls.Add(context);
        private PredictionResult? _pending;
        public void Complete(string word) => _pending = new PredictionResult(word, 40, PredictionType.LMSuggestion, 0.4);

        public IReadOnlyList<PredictionResult>? TryGetResults()
        {
            var result = _pending;
            _pending = null;
            return result is null ? null : [result];
        }

        public void Cancel() { }
        public void Dispose() { }
    }

    private static readonly HashSet<string> SeedWords = new() { "hallo", "halle", "haus" };

    private sealed class Rig : IDisposable
    {
        public KeyboardViewModel Vm { get; }
        public FakeSensitive Sensitive { get; } = new();
        public FakePoll Poll { get; } = new();
        public FakeLm Lm { get; } = new();
        public PredictionOrchestrator Orchestrator { get; }
        public int KeysSent;
        public string Sent = "";

        public Rig()
        {
            var messenger = new WeakReferenceMessenger();
            messenger.Register<Rig, KeyActivatedMessage>(this, (r, m) => { r.KeysSent++; r.Sent += m.Key; });
            Vm = new KeyboardViewModel(
                new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } }, messenger,
                sensitiveContext: Sensitive, sensitivePoll: Poll);
            Orchestrator = new PredictionOrchestrator(new WordPredictor(new InMemoryDictProvider()), lmPredictor: Lm);
            Vm.SetOrchestrator(Orchestrator);
            Vm.IsVisible = true;
        }

        /// <summary>The keyboard shows: one poll at the show moment, awaited so the verdict is applied.</summary>
        public Task ShowAsync() => Vm.BeginSensitiveCheck("show");

        /// <summary>Types the text; after every key the poll the typing path started has landed.</summary>
        public async Task TypeAsync(string text)
        {
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                Vm.ActivateKey((string)e.Current, null);
                await Vm.LastSensitivePoll.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
            }
        }

        public async Task SpaceAsync()
        {
            Vm.HandleSpace();
            await Vm.LastSensitivePoll.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        }

        public IEnumerable<string> LearnedWords => Orchestrator.WordPredictor.WordFrequencies.Keys
            .Where(w => !SeedWords.Contains(w));

        public List<string> Pairs() => Orchestrator.WordPredictor.CaptureLearnedState().Bigrams
            .SelectMany(b => b.Value.Keys.Select(next => b.Key + "->" + next)).ToList();

        public void Dispose() => Vm.Dispose();
    }

    private static void AssertNothingLearned(Rig rig, string because)
    {
        Check.WithCustomMessage($"{because}: words learned: " + string.Join(",", rig.LearnedWords))
            .That(rig.LearnedWords).IsEmpty();
        Check.WithCustomMessage($"{because}: pairs learned: " + string.Join(",", rig.Pairs()))
            .That(rig.Pairs()).IsEmpty();
    }

    // ---- poll sensitive, event path says not sensitive ---------------------------------------------

    [Fact]
    public async Task PollSensitive_WhileTheEventPathSaysNot_TheBarIsEmptyAndLocked_AndNothingIsLearned()
    {
        using var rig = new Rig();
        rig.Poll.Read = () => true;                // the direct read sees the password field
        rig.Sensitive.IsSensitiveActive = false;   // the event path never flagged it (the PW1 shape)

        await rig.ShowAsync();

        Check.That(rig.Vm.IsSensitiveContext).IsTrue();
        Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();

        await rig.TypeAsync("ha");
        Check.WithCustomMessage("no word may be offered in a password field")
            .That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();
        await rig.TypeAsync("xyzzy");
        await rig.SpaceAsync();
        await rig.TypeAsync("quux");
        rig.Vm.HandleAddWord();
        await rig.SpaceAsync();
        AssertNothingLearned(rig, "a poll-sensitive field");
        Check.WithCustomMessage("no LM prompt was built: " + string.Join("|", rig.Lm.NextWordCalls))
            .That(rig.Lm.NextWordCalls).IsEmpty();
        Check.That(rig.Lm.CompletionCalls).IsEmpty();
    }

    [Fact]
    public async Task TheEventPath_CannotClearThePollsSensitiveVerdict()
    {
        // MUTANT PIN (let the event path clear the poll's verdict): the flag going back to false must not
        // unlock the bar while the poll said password.
        using var rig = new Rig();
        rig.Poll.Read = () => true;
        rig.Sensitive.IsSensitiveActive = true;
        await rig.ShowAsync();

        rig.Sensitive.IsSensitiveActive = false;   // a later focus event flips the cached flag back

        Check.That(rig.Vm.IsSensitiveContext).IsTrue();
        Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();
        Check.That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();
    }

    [Fact]
    public async Task TheEventPath_CanStillRaise_EvenWhenThePollSaysNotSensitive()
    {
        using var rig = new Rig();
        rig.Poll.Read = () => false;
        await rig.ShowAsync();
        Check.That(rig.Vm.IsSuggestionsLocked).IsFalse();

        rig.Sensitive.IsSensitiveActive = true;

        Check.That(rig.Vm.IsSensitiveContext).IsTrue();
        Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();
        Check.That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();
    }

    // ---- poll unknown ---------------------------------------------------------------------------------

    private static readonly string[] UnknownKinds = ["error", "timeout"];

    public static IEnumerable<object[]> UnknownKindData() => UnknownKinds.Select(k => new object[] { k });

    private static (Rig Rig, ManualResetEventSlim Release) UnknownRig(string kind)
    {
        var rig = new Rig();
        var release = new ManualResetEventSlim(false);
        rig.Poll.Read = kind == "error"
            ? () => throw new InvalidOperationException("UIA fault")
            : () => { release.Wait(HangGuard); return false; };   // never answers within the 300 ms box
        return (rig, release);
    }

    [Theory]
    [MemberData(nameof(UnknownKindData))]
    public async Task PollUnknown_TheBarIsEmptyAndLocked_NothingIsLearned_AndTheLmIsNeverAsked(string kind)
    {
        // MUTANT PIN (drop the unknown branch): an unanswered poll must lock, not read as "not sensitive".
        var (rig, release) = UnknownRig(kind);
        using var _ = rig;
        try
        {
            await rig.ShowAsync();

            Check.That(rig.Vm.IsSensitiveContext).IsFalse();     // nothing says "password" ...
            Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();     // ... but the field is unverified: fail closed
            Check.That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();

            await rig.TypeAsync("ha");
            Check.That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();
            await rig.TypeAsync("xyzzy");
            await rig.SpaceAsync();
            await rig.TypeAsync("quux");
            await rig.SpaceAsync();
            AssertNothingLearned(rig, "an unknown poll (" + kind + ")");
            Check.That(rig.Lm.NextWordCalls).IsEmpty();
            Check.That(rig.Lm.CompletionCalls).IsEmpty();
            Check.That(rig.Vm.TryTakeNextWordSuggestions()).IsNull();
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task PollUnknown_ANextWordAnswerThatArrivesAfterwards_IsDroppedNotHandedOver()
    {
        // MUTANT PIN (the next-word hand-over honours only the password case): an answer for a boundary
        // reserved while the field was still verified must not surface once the poll can no longer vouch for it.
        using var rig = new Rig();
        rig.Poll.Read = () => false;
        await rig.ShowAsync();
        await rig.TypeAsync("haus");
        await rig.SpaceAsync();
        rig.Vm.GetSuggestionsWithContext();
        Check.That(rig.Vm.NextWordSlotReserved).IsTrue();

        rig.Poll.Read = () => throw new InvalidOperationException("UIA fault");
        await rig.Vm.BeginSensitiveCheck("hide");
        rig.Lm.Complete("leer");

        Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();
        Check.That(rig.Vm.TryTakeNextWordSuggestions()).IsNull();
        Check.That(rig.Vm.NextWordSlotReserved).IsFalse();
    }

    [Theory]
    [MemberData(nameof(UnknownKindData))]
    public async Task PollUnknown_TypingStillGoesThrough_EveryKeyIsSent(string kind)
    {
        var (rig, release) = UnknownRig(kind);
        using var _ = rig;
        try
        {
            await rig.ShowAsync();

            await rig.TypeAsync("hallo");
            await rig.SpaceAsync();
            await rig.TypeAsync("welt");

            Check.That(rig.Sent).IsEqualTo("hallo welt");
            Check.That(rig.KeysSent).IsEqualTo(10);
            Check.That(rig.Vm.CurrentInput).IsEqualTo("welt");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ABlockedPoll_NeverMakesATypedKeyWait()
    {
        using var release = new ManualResetEventSlim(false);
        using var rig = new Rig();
        rig.Poll.Read = () => { release.Wait(HangGuard); return false; };
        try
        {
            // The key handler runs on a worker so a regression that waited for the poll cannot hang the
            // suite: it is the hang guard that would trip, never a timing assertion.
            await Task.Run(() =>
            {
                rig.Vm.ActivateKey("a", null);
                rig.Vm.ActivateKey("b", null);
                rig.Vm.HandleSpace();
            }).WaitAsync(HangGuard, TestContext.Current.CancellationToken);

            Check.That(rig.Sent).IsEqualTo("ab ");
            Check.WithCustomMessage("the poll is still blocked, so the keys did not wait for it")
                .That(release.IsSet).IsFalse();
        }
        finally
        {
            release.Set();
        }
    }

    // ---- poll not sensitive: normal -----------------------------------------------------------------

    [Fact]
    public async Task PollNotSensitive_EverythingIsNormal()
    {
        using var rig = new Rig();
        rig.Poll.Read = () => false;
        await rig.ShowAsync();

        Check.That(rig.Vm.IsSensitiveContext).IsFalse();
        Check.That(rig.Vm.IsSuggestionsLocked).IsFalse();

        await rig.TypeAsync("ha");
        Check.WithCustomMessage("normal field: words are offered")
            .That(rig.Vm.GetSuggestionsWithContext().Select(r => r.Word)).Contains("hallo");

        await rig.TypeAsync("xyzzy");
        await rig.SpaceAsync();
        await rig.TypeAsync("quux");
        await rig.SpaceAsync();

        Check.That(rig.LearnedWords).Contains("haxyzzy");
        Check.That(rig.Pairs()).Contains("haxyzzy->quux");
        rig.Vm.GetSuggestionsWithContext();
        Check.WithCustomMessage("normal field: the LM is asked for the next word")
            .That(rig.Lm.NextWordCalls.Count).IsStrictlyGreaterThan(0);
    }

    [Fact]
    public async Task ANoneAnswer_IsNotSensitive_BecauseTheTicketListsOnlyTimeoutBusyAndError()
    {
        using var rig = new Rig();
        rig.Poll.Read = () => null;
        await rig.ShowAsync();

        Check.That(rig.Vm.IsSuggestionsLocked).IsFalse();
    }

    // ---- a newer poll replaces an older verdict -------------------------------------------------------

    [Fact]
    public async Task ANewerPoll_ClearsAnUnknown_AndAPasswordVerdict()
    {
        using var rig = new Rig();
        var answers = new Queue<Func<bool?>>(new Func<bool?>[]
        {
            () => throw new InvalidOperationException("fault"),
            () => true,
            () => false,
        });
        rig.Poll.Read = () => answers.Dequeue()();

        await rig.ShowAsync();
        Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();     // unknown
        await rig.Vm.BeginSensitiveCheck("hide");
        Check.That(rig.Vm.IsSensitiveContext).IsTrue();      // password
        await rig.Vm.BeginSensitiveCheck("show");
        Check.That(rig.Vm.IsSuggestionsLocked).IsFalse();    // a newer poll says it is a normal field
    }

    // ---- every learn path honours the verdict ---------------------------------------------------------

    public static IEnumerable<object[]> LockedModes() =>
    [
        ["event"],
        ["poll-sensitive"],
        ["poll-unknown"],
    ];

    private static async Task Lock(Rig rig, string mode)
    {
        switch (mode)
        {
            case "event":
                rig.Sensitive.IsSensitiveActive = true;
                break;
            case "poll-sensitive":
                rig.Poll.Read = () => true;
                await rig.ShowAsync();
                break;
            default:
                rig.Poll.Read = () => throw new InvalidOperationException("fault");
                await rig.ShowAsync();
                break;
        }
        Check.That(rig.Vm.IsSuggestionsLocked).IsTrue();
    }

    [Theory]
    [MemberData(nameof(LockedModes))]
    public async Task LearnPath_SpaceCommit_LearnsNoWordAndNoPair(string mode)
    {
        using var rig = new Rig();
        await Lock(rig, mode);

        await rig.TypeAsync("eins");
        await rig.SpaceAsync();
        await rig.TypeAsync("zwei");
        await rig.SpaceAsync();

        AssertNothingLearned(rig, "Space commit, mode " + mode);
    }

    [Theory]
    [MemberData(nameof(LockedModes))]
    public async Task LearnPath_PairOnly_LearnsNoPair(string mode)
    {
        // Isolates LearnPair: the previous word is a SEED word (already in the dictionary), so only the
        // pair gate can keep the pair out.
        using var rig = new Rig();
        await Lock(rig, mode);

        await rig.TypeAsync("haus");
        await rig.SpaceAsync();
        await rig.TypeAsync("hallo");
        await rig.SpaceAsync();

        Check.WithCustomMessage("pairs learned: " + string.Join(",", rig.Pairs())).That(rig.Pairs()).IsEmpty();
    }

    [Theory]
    [MemberData(nameof(LockedModes))]
    public async Task LearnPath_AMultiWordPick_LearnsNothing(string mode)
    {
        using var rig = new Rig();
        await Lock(rig, mode);

        rig.Vm.HandlePredictionSelected("alpha beta gamma");

        AssertNothingLearned(rig, "multi-word pick, mode " + mode);
    }

    [Theory]
    [MemberData(nameof(LockedModes))]
    public async Task LearnPath_AddWord_LearnsNothing(string mode)
    {
        using var rig = new Rig();
        await Lock(rig, mode);

        await rig.TypeAsync("zebrafink");
        rig.Vm.HandleAddWord();

        AssertNothingLearned(rig, "Add Word, mode " + mode);
    }

    [Theory]
    [MemberData(nameof(LockedModes))]
    public async Task LearnPath_ASingleWordPick_LearnsNothing(string mode)
    {
        using var rig = new Rig();
        await Lock(rig, mode);

        rig.Vm.HandlePredictionSelected("zebrafink");

        AssertNothingLearned(rig, "single-word pick, mode " + mode);
    }

    // ---- the typing path re-asks the field -----------------------------------------------------------

    [Fact]
    public async Task TheTypingPath_PollsAtTheStartAndTheCommitOfEveryWord_AndNotInBetween()
    {
        // MUTANT PIN (drop the typing-path polls): without them a field switched to while the keyboard stays
        // up is never re-checked.
        using var rig = new Rig();

        await rig.TypeAsync("a");
        Check.WithCustomMessage("the first letter of a word polls").That(rig.Poll.Reads).IsEqualTo(1);
        await rig.TypeAsync("bc");
        Check.WithCustomMessage("letters inside the word do not").That(rig.Poll.Reads).IsEqualTo(1);
        await rig.SpaceAsync();
        Check.WithCustomMessage("the commit polls").That(rig.Poll.Reads).IsEqualTo(2);
        await rig.TypeAsync("d");
        Check.That(rig.Poll.Reads).IsEqualTo(3);
        rig.Vm.HandleEnter();
        await rig.Vm.LastSensitivePoll.WaitAsync(HangGuard, TestContext.Current.CancellationToken);
        Check.WithCustomMessage("Enter commits the word too").That(rig.Poll.Reads).IsEqualTo(4);
    }

    [Fact]
    public async Task TheFirstWordAfterAPasswordField_KeepsItsFirstLetterInTheMirror()
    {
        // The poll about the NEW field lands one key after the word opened. Ending the session on that late
        // "not sensitive" would drop the first letter from the mirror, and a pick would then delete one
        // character too few ("hhallo").
        using var rig = new Rig();
        var inPasswordField = true;
        rig.Poll.Read = () => inPasswordField;
        await rig.ShowAsync();
        await rig.TypeAsync("geheim");
        await rig.SpaceAsync();

        inPasswordField = false;
        await rig.TypeAsync("ha");

        Check.That(rig.Vm.IsSuggestionsLocked).IsFalse();
        Check.WithCustomMessage("the mirror must hold both letters").That(rig.Vm.CurrentInput).IsEqualTo("ha");
        Check.WithCustomMessage("nothing of the password is left in the mirror")
            .That(rig.Vm.RecentText).IsEqualTo("");
    }

    [Fact]
    public async Task SwitchingFiveTimesBetweenAPasswordAndANormalField_LocksEveryTimeAndNeverOffersAWord()
    {
        // The PW1 sitting in miniature: the keyboard stays up, the focus switches between field 1 (password)
        // and field 2 (normal). The poll answers for whichever field has focus; the event path never helps.
        using var rig = new Rig();
        var inPasswordField = false;
        rig.Poll.Read = () => inPasswordField;

        for (var round = 0; round < 5; round++)
        {
            inPasswordField = true;                       // click into field 1
            await rig.TypeAsync("ha");                    // the first letter's poll lands before the next key
            Check.WithCustomMessage($"round {round}: locked in the password field").That(rig.Vm.IsSuggestionsLocked).IsTrue();
            Check.WithCustomMessage($"round {round}: no word in field 1").That(rig.Vm.GetSuggestionsWithContext()).IsEmpty();
            await rig.TypeAsync("x");
            await rig.SpaceAsync();

            inPasswordField = false;                      // click into field 2
            await rig.TypeAsync("ha");
            Check.WithCustomMessage($"round {round}: unlocked in the normal field").That(rig.Vm.IsSuggestionsLocked).IsFalse();
            Check.WithCustomMessage($"round {round}: words in field 2")
                .That(rig.Vm.GetSuggestionsWithContext().Select(r => r.Word)).Contains("hallo");
            await rig.TypeAsync("y");
            await rig.SpaceAsync();
        }

        Check.WithCustomMessage("the word typed in field 1 was never learned: " + string.Join(",", rig.LearnedWords))
            .That(rig.LearnedWords).Not.Contains("hax");
        Check.WithCustomMessage("the word typed in field 2 was learned").That(rig.LearnedWords).Contains("hay");
    }
}
