namespace EyeCtrl.ViewModels.Tests;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// Typed word pairs are learned on the commit paths (Space, a single-word selection, and the
/// joining pair of a multi-word selection), normalized so a sentence-start capital or a trailing
/// comma still finds the pair. Single key presses are no longer learned as words.
/// </summary>
public class KeyboardViewModelPairLearningTests
{
    private sealed class InMemoryDictProvider : IDictionaryProvider
    {
        public Dictionary<string, int> Load() => [];
        public void Save(Dictionary<string, int> words) { }
    }

    private static (KeyboardViewModel Vm, PredictionOrchestrator Orchestrator) Create()
    {
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        var predictor = new WordPredictor(new InMemoryDictProvider());
        var orchestrator = new PredictionOrchestrator(predictor);
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        vm.SetOrchestrator(orchestrator);
        return (vm, orchestrator);
    }

    private static (KeyboardViewModel Vm, PredictionOrchestrator Orchestrator) CreateWithAbbreviations(string dir)
    {
        var basePath = Path.Combine(dir, "base_dictionary_en.json");
        File.WriteAllText(basePath,
            "{\"words\":{\"hello\":100},\"abbreviations\":[\"usw.\",\"bzw.\",\"ca.\",\"dr.\"]}");
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        var predictor = new WordPredictor(new FileDictionaryProvider(basePath, dir, "en"));
        var orchestrator = new PredictionOrchestrator(predictor);
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        vm.SetOrchestrator(orchestrator);
        return (vm, orchestrator);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eyectrl_abbr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Type(KeyboardViewModel vm, string text)
    {
        foreach (var ch in text) vm.ActivateKey(ch.ToString(), null);
    }

    private static void TypeWordAndSpace(KeyboardViewModel vm, string word)
    {
        Type(vm, word);
        vm.HandleSpace();
    }

    private static List<string> BarFor(KeyboardViewModel vm, PredictionOrchestrator orchestrator, string? context)
        => orchestrator.GetSuggestions(vm.CurrentInput, context, maxCount: 10).Select(r => r.Word).ToList();

    [Fact]
    public void TypedPair_ShowsAfterTheSecondUse_NotAfterTheFirst()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            TypeWordAndSpace(vm, "ich");
            TypeWordAndSpace(vm, "möchte");
            Check.WithCustomMessage("a pair shows only after its second use")
                .That(BarFor(vm, orchestrator, "ich")).Not.Contains("möchte");

            TypeWordAndSpace(vm, "ich");
            TypeWordAndSpace(vm, "möchte");
            Check.That(BarFor(vm, orchestrator, "ich")).Contains("möchte");
        }
    }

    [Fact]
    public void TypedPair_IsCaseInsensitive()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "Ich");
                TypeWordAndSpace(vm, "möchte");
            }

            // Mid-sentence lowercase context still finds the pair learned from "Ich".
            Check.That(BarFor(vm, orchestrator, "ich")).Contains("möchte");
        }
    }

    [Fact]
    public void TypedPair_TrimsTrailingPunctuation()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "Hallo,");
                TypeWordAndSpace(vm, "wie");
            }

            Check.That(BarFor(vm, orchestrator, "hallo")).Contains("wie");
        }
    }

    [Fact]
    public void SentenceEnd_LearnsNoPair()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "morgen.");
                TypeWordAndSpace(vm, "Ich");
            }

            // "morgen." ends a sentence: the next word is not a pair for it.
            Check.That(BarFor(vm, orchestrator, "morgen")).Not.Contains("Ich");
        }
    }

    [Fact]
    public void Enter_NoPairFromBeforeTheLineBreak()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "ich");
                vm.HandleEnter();
                TypeWordAndSpace(vm, "möchte");
                vm.HandleEnter();
            }

            Check.That(BarFor(vm, orchestrator, "ich")).Not.Contains("möchte");
        }
    }

    [Fact]
    public void DeletionIntoCommittedText_ReopensTheWord_SoTheNextWordIsNotItsPair()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "ich");
                vm.HandleBackspace();            // eats the space: 'ich' is the open word again
                TypeWordAndSpace(vm, "möchte");  // ... so this continues it: the app shows "ichmöchte"
            }

            Check.WithCustomMessage("'möchte' is glued to 'ich' in the app, not its follower")
                .That(BarFor(vm, orchestrator, "ich")).Not.Contains("möchte");
        }
    }

    [Fact]
    public void SingleWordSelection_LearnsTheTypedPair()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            TypeWordAndSpace(vm, "ich");
            vm.HandlePredictionSelected("möchte");
            TypeWordAndSpace(vm, "ich");
            vm.HandlePredictionSelected("möchte");

            Check.That(BarFor(vm, orchestrator, "ich")).Contains("möchte");
        }
    }

    [Fact]
    public void MultiWordSelection_LearnsTheJoiningPairAndNotTheWordsTwice()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            TypeWordAndSpace(vm, "ich");
            vm.HandlePredictionSelected("möchte gerne");
            TypeWordAndSpace(vm, "ich");
            vm.HandlePredictionSelected("möchte gerne");

            Check.That(BarFor(vm, orchestrator, "ich")).Contains("möchte");
            // The internal pair of the selection is LearnSequence's job.
            Check.That(BarFor(vm, orchestrator, "möchte")).Contains("gerne");
            // The joining pair must not re-learn the words: two selections, two learns.
            Check.That(orchestrator.WordPredictor.WordFrequencies["möchte"]).IsEqualTo(2);
        }
    }

    [Theory]
    [InlineData("Ende.\"")]
    [InlineData("Ende.)")]
    [InlineData("Ende.»")]
    public void SentenceEndBehindAClosingQuoteOrBracket_LearnsNoPair(string previousWord)
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, previousWord);
                TypeWordAndSpace(vm, "Ich");
            }

            Check.That(BarFor(vm, orchestrator, "ende")).Not.Contains("Ich");
        }
    }

    [Fact]
    public void ClosingQuoteWithoutASentenceEnd_StillLearnsThePair()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "\"Hallo\"");
                TypeWordAndSpace(vm, "du");
            }

            Check.That(BarFor(vm, orchestrator, "hallo")).Contains("du");
        }
    }

    [Fact]
    public void TypedWordWithTrailingPunctuation_IsLearnedWithoutIt()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            for (var round = 0; round < 2; round++)
            {
                TypeWordAndSpace(vm, "und");
                TypeWordAndSpace(vm, "Schöneck,");
            }

            // The pair key is normalised, so the learned word must be trimmed too or the
            // dictionary gate can never find it.
            var afterUnd = BarFor(vm, orchestrator, "und");
            Check.That(afterUnd).Contains("Schöneck");
            Check.That(afterUnd).Not.Contains("Schöneck,");

            vm.CurrentInput = "Schö";
            Check.That(BarFor(vm, orchestrator, null)).Contains("Schöneck");
        }
    }

    [Fact]
    public void PickedWordWithTrailingPunctuation_IsLearnedWithoutIt()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            vm.HandlePredictionSelected("Schöneck,");

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("schöneck")).IsTrue();
            Check.That(words.ContainsKey("schöneck,")).IsFalse();
        }
    }

    [Theory]
    [InlineData("usw.")]
    [InlineData("z.B.")]
    [InlineData("bzw.")]
    [InlineData("d.h.")]
    [InlineData("z.T.")]
    [InlineData("u.U.")]
    [InlineData("Bzw.")]
    public void AbbreviationWithPeriod_KeepsItsPeriodWhenTyped(string abbreviation)
    {
        var dir = NewTempDir();
        try
        {
            var (vm, orchestrator) = CreateWithAbbreviations(dir);
            using (vm)
            {
                TypeWordAndSpace(vm, abbreviation);

                vm.CurrentInput = abbreviation[..2];
                Check.WithCustomMessage(
                        "the learned abbreviation lost its period, so picking it would type it without one")
                    .That(BarFor(vm, orchestrator, null)).Contains(abbreviation);
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AbbreviationWithPeriod_KeepsItsPeriodWhenPicked()
    {
        var dir = NewTempDir();
        try
        {
            var (vm, orchestrator) = CreateWithAbbreviations(dir);
            using (vm)
            {
                vm.HandlePredictionSelected("usw.");

                vm.CurrentInput = "us";
                Check.That(BarFor(vm, orchestrator, null)).Contains("usw.");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AbbreviationsInAMultiWordSelection_KeepTheirPeriods()
    {
        var dir = NewTempDir();
        try
        {
            var (vm, orchestrator) = CreateWithAbbreviations(dir);
            using (vm)
            {
                vm.HandlePredictionSelected("usw. z.B. bzw. d.h.");

                var words = orchestrator.WordPredictor.WordFrequencies;
                Check.That(words.ContainsKey("usw.")).IsTrue();
                Check.That(words.ContainsKey("z.b.")).IsTrue();
                Check.That(words.ContainsKey("bzw.")).IsTrue();
                Check.That(words.ContainsKey("d.h.")).IsTrue();
                Check.That(words.ContainsKey("usw")).IsFalse();
                Check.That(words.ContainsKey("z.b")).IsFalse();
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void SymbolTermThatTrimsToOneCharacter_IsNotLearned()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            TypeWordAndSpace(vm, "C#");
            TypeWordAndSpace(vm, ":-)");

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("c")).IsFalse();
            Check.That(words.ContainsKey("c#")).IsFalse();
            Check.That(words.ContainsKey(":-)")).IsFalse();
        }
    }

    [Theory]
    [InlineData("max@web.de.", "max@web.de")]
    [InlineData("v1.2.", "v1.2")]
    [InlineData("www.example.de.", "www.example.de")]
    [InlineData("3.5.", "3.5")]
    public void DottedNonAbbreviation_LosesTheFinalPeriod(string raw, string expectedKey)
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            TypeWordAndSpace(vm, raw);

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey(expectedKey)).IsTrue();
            Check.That(words.ContainsKey(expectedKey + ".")).IsFalse();
        }
    }

    [Theory]
    [InlineData("z.B.,", "z.b.")]
    [InlineData("usw.)", "usw.")]
    [InlineData("(bzw.)", "bzw.")]
    [InlineData("ca.:", "ca.")]
    public void AbbreviationFollowedByPunctuation_KeepsItsPeriod(string raw, string expectedKey)
    {
        var dir = NewTempDir();
        try
        {
            var (vm, orchestrator) = CreateWithAbbreviations(dir);
            using (vm)
            {
                TypeWordAndSpace(vm, raw);

                var words = orchestrator.WordPredictor.WordFrequencies;
                Check.WithCustomMessage("the period after the trimmed span was missed")
                    .That(words.ContainsKey(expectedKey)).IsTrue();
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AbbreviationTypedWithoutADot_DoesNotGainOne()
    {
        var dir = NewTempDir();
        try
        {
            var (vm, orchestrator) = CreateWithAbbreviations(dir);
            using (vm)
            {
                TypeWordAndSpace(vm, "ca");

                var words = orchestrator.WordPredictor.WordFrequencies;
                Check.That(words.ContainsKey("ca")).IsTrue();
                Check.That(words.ContainsKey("ca.")).IsFalse();
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AllCapsAbbreviation_KeepsItsPeriod()
    {
        var dir = NewTempDir();
        try
        {
            var (vm, orchestrator) = CreateWithAbbreviations(dir);
            using (vm)
            {
                TypeWordAndSpace(vm, "USW.");

                var words = orchestrator.WordPredictor.WordFrequencies;
                Check.That(words.ContainsKey("usw.")).IsTrue();
                Check.That(words.ContainsKey("usw")).IsFalse();
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AddWord_KeepsADeliberatePeriodAfterALettersWord()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            vm.CurrentInput = "Bsp.";
            vm.HandleAddWord();

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("bsp.")).IsTrue();
            Check.That(words.ContainsKey("bsp")).IsFalse();
        }
    }

    [Fact]
    public void AddWord_StripsOtherTrailingPunctuation()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            vm.CurrentInput = "Bsp,";
            vm.HandleAddWord();

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("bsp")).IsTrue();
            Check.That(words.ContainsKey("bsp,")).IsFalse();
        }
    }

    [Fact]
    public void AddWord_TrimsTheWord()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            vm.CurrentInput = "Schöneck,";
            vm.HandleAddWord();

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("schöneck")).IsTrue();
            Check.That(words.ContainsKey("schöneck,")).IsFalse();
        }
    }

    [Fact]
    public void MultiWordSelection_TrimsEveryToken()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            vm.HandlePredictionSelected("Herr Schöneck,");

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("herr")).IsTrue();
            Check.That(words.ContainsKey("schöneck")).IsTrue();
            Check.That(words.ContainsKey("schöneck,")).IsFalse();
        }
    }

    [Fact]
    public void TypingLetters_LearnsNoWordUntilSpace()
    {
        var (vm, orchestrator) = Create();
        using (vm)
        {
            Type(vm, "hallo");

            var words = orchestrator.WordPredictor.WordFrequencies;
            Check.That(words.ContainsKey("hallo")).IsFalse();
            Check.That(words.ContainsKey("h")).IsFalse();
            Check.That(words.ContainsKey("a")).IsFalse();

            vm.HandleSpace();

            Check.That(orchestrator.WordPredictor.WordFrequencies["hallo"]).IsEqualTo(1);
        }
    }
}
