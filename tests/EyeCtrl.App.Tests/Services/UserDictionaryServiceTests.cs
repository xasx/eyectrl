namespace EyeCtrl.App.Tests.Services;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

/// <summary>
/// UserDictionaryService — the settings page's learned-words view over the merged user
/// dictionary file (learned = user-file keys minus base keys), with deletions routed through
/// the LIVE prediction stack (end-to-end over a real synchronous PredictionStackFactory here).
/// </summary>
public class UserDictionaryServiceTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _appDataDir;
    private readonly EyeCtrlSettings _settings = new();

    public UserDictionaryServiceTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "eyectrl_dict_" + Guid.NewGuid());
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl_dict_app_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_baseDir, "Resources", "Dictionaries"));
        Directory.CreateDirectory(_appDataDir);
        _settings.Keyboard.LanguagePack = "en";
        File.WriteAllText(
            Path.Combine(_baseDir, "Resources", "Dictionaries", "en.json"),
            JsonSerializer.Serialize(new { words = new Dictionary<string, int> { ["hello"] = 100, ["help"] = 90 } }));
    }

    private string UserFile => Path.Combine(_appDataDir, "user_dictionary_en.json");

    private void WriteUserFile(Dictionary<string, int> words) =>
        File.WriteAllText(UserFile, JsonSerializer.Serialize(words));

    private UserDictionaryService Create() => new(_settings, _appDataDir, _baseDir);

    [Fact]
    public void GetLearnedWords_ListsUserFileEntriesNotInBase_MostUsedFirst()
    {
        // "zeta" outranks "alpha" by use but loses alphabetically - the pair makes the
        // ordering assertion fail if either sort key is dropped (a fixture where frequency
        // and alphabetical order agree cannot fail for the reason it names).
        WriteUserFile(new() { ["hello"] = 120, ["zeta"] = 9, ["alpha"] = 5 });
        var service = Create();

        // "hello" is base vocabulary that merely got used (its merged presence is frequency
        // inflation, not a dictionary entry the user added); the two novel words are learned.
        Check.That(service.GetLearnedWords()).ContainsExactly("zeta", "alpha");
    }

    [Fact]
    public void GetLearnedWords_NoUserFile_IsEmpty()
    {
        Check.That(Create().GetLearnedWords()).IsEmpty();
    }

    [Fact]
    public void GetLearnedWords_HidesSingleCharactersAndPunctuation()
    {
        // Review finding 3, measured on the shipped layouts: every keystroke the predictor sees
        // is learned incidentally, so the merged file collects ~50 single-char/punctuation rows
        // per language. None of those are words a user can decide about.
        WriteUserFile(new() { ["hello"] = 120, ["!"] = 900, ["q"] = 800, ["well-known"] = 7, ["zeppelin"] = 5 });
        var service = Create();

        Check.That(service.GetLearnedWords()).ContainsExactly("zeppelin");
    }

    [Fact]
    public void ForgetWord_RemovesTheWordFromTheFile()
    {
        WriteUserFile(new() { ["hello"] = 120, ["zeppelin"] = 5, ["aardvark"] = 9 });
        var service = Create();

        service.ForgetWord("zeppelin");

        Check.That(service.GetLearnedWords()).ContainsExactly("aardvark");
        // Base entries survive the rewrite.
        var onDisk = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(UserFile))!;
        Check.That(onDisk).ContainsKey("hello");
        Check.That(onDisk.ContainsKey("zeppelin")).IsFalse();
    }

    [Fact]
    public void GetLearnedWords_PrefersTheLiveDictionary_SeesUnsavedAddWordLearns()
    {
        // Review finding 4: LearnWord does not persist, so a file-only view could not list an
        // Add Word learn until the next rebuild/exit - breaking D3's reversibility premise.
        // The live dictionary (when attached) is the source of truth instead.
        WriteUserFile(new() { ["hello"] = 120 });
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        var service = Create();
        service.SetFactory(factory);

        orchestrator!.WordPredictor.LearnWord("zeppelin"); // learned, NOT saved
        Check.That(service.GetLearnedWords()).ContainsExactly("zeppelin");
    }

    [Fact]
    public void ForgetWord_MissingUserFile_StillForgetsTheLiveStack()
    {
        // Review finding 5: the file half is a no-op (no file), so an early return would strand
        // the word in predictor memory - and the caller learns the rewrite never happened.
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        orchestrator!.WordPredictor.LearnWord("zeppelin");
        var service = Create();
        service.SetFactory(factory);

        Check.That(service.ForgetWord("zeppelin")).IsFalse(); // no file to rewrite
        Check.That(orchestrator.WordPredictor.GetSuggestions("zep")
            .Select(w => w.Word)).Not.Contains("zeppelin"); // live half still ran
    }

    [Fact]
    public void ForgetWord_GoesThroughTheLiveStack_EndToEnd()
    {
        WriteUserFile(new() { ["hello"] = 120, ["zeppelin"] = 5 });
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        Check.That(orchestrator).IsNotNull();
        Check.That(orchestrator!.WordPredictor.GetSuggestions("zep")
            .Select(s => s.Word)).Contains("zeppelin");

        var service = Create();
        service.SetFactory(factory);

        service.ForgetWord("zeppelin");

        Check.That(orchestrator.WordPredictor.GetSuggestions("zep")
            .Select(s => s.Word)).Not.Contains("zeppelin");
    }

    [Fact]
    public void ClearLearned_RemovesEveryLearnedWord_KeepsBaseEntriesOnDisk()
    {
        WriteUserFile(new() { ["hello"] = 120, ["zeppelin"] = 5, ["aardvark"] = 9 });
        var service = Create();

        service.ClearLearned();

        Check.That(service.GetLearnedWords()).IsEmpty();
        var onDisk = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(UserFile))!;
        Check.That(onDisk.Keys).ContainsExactly("hello");
    }

    [Fact]
    public void FactoryForgetWords_ArmsTombstonesThatTheSwapDrains()
    {
        // The in-flight-rebuild guard: a word forgotten while a background build was loading the
        // pre-forget disk state (stamp >= that build's generation) is removed from the freshly
        // swapped-in stack too, and the tombstone dies with that swap.
        using var factory = new PredictionStackFactory();
        factory.ForgetWords(["zeppelin"]); // no live stack: tombstone only, stamped gen 0

        var predictor = new WordPredictor(new HelloProvider());
        predictor.LearnWord("zeppelin");
        factory.ApplyPendingForgetsTo(predictor, buildGeneration: 0); // the racing build's swap
        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Not.Contains("zeppelin");

        predictor.LearnWord("zeppelin"); // re-learn AFTER the swap must not be tombstoned again
        factory.ApplyPendingForgetsTo(predictor, buildGeneration: 0);
        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Contains("zeppelin");
    }

    [Fact]
    public void FactoryForgetWords_LaterBuildDoesNotDrain_ARelearnSurvivesItsSwap()
    {
        // Review finding 2: a tombstone armed when NO build was in flight must not fire at an
        // arbitrary later swap - that build loaded the already-rewritten files, and a re-learn
        // that happened in between must survive it.
        using var factory = new PredictionStackFactory();
        factory.ForgetWords(["zeppelin"]); // stamped at generation 0, nothing in flight

        var predictor = new WordPredictor(new HelloProvider());
        predictor.LearnWord("zeppelin"); // re-learn BEFORE the later build's swap
        factory.ApplyPendingForgetsTo(predictor, buildGeneration: 1); // a NEWER build swaps in
        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Contains("zeppelin");

        // And the stamp is consumed/expired, not left armed for yet another swap.
        predictor.ForgetWord("zeppelin");
        predictor.LearnWord("zeppelin");
        factory.ApplyPendingForgetsTo(predictor, buildGeneration: 2);
        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Contains("zeppelin");
    }

    [Fact]
    public void ForgetWords_RepeatedForgetAfterARelearn_ReachesTheLiveStackAgain()
    {
        // Review finding 1, reproduced end-to-end: forget, re-learn, forget again - the second
        // forget must still remove the word from the live stack even though its tombstone was
        // already armed. The page reload lists the word (a save put it back on disk), the user
        // deletes it, and the live stack must obey.
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        var predictor = orchestrator!.WordPredictor;

        factory.ForgetWords(["zeppelin"]);
        predictor.LearnWord("zeppelin"); // re-learn (Add Word or typing), then some later save
        predictor.SaveUserDictionary();   // puts it back on disk

        factory.ForgetWords(["zeppelin"]); // the repeated forget
        Check.That(predictor.GetSuggestions("zep").Select(w => w.Word)).Not.Contains("zeppelin");
    }

    [Fact]
    public async Task ForgetWords_DuringABackgroundBuild_TheProductionSwapPathDrainsIt()
    {
        // The gap round 1 named: no test executed the production background path
        // (Rebuild -> build task -> continuation -> SwapInWithLearnedTail -> drain). The
        // postToUi seam makes the in-flight window DETERMINISTIC: swaps are enqueued, not
        // run, so a forget issued after the continuation queued (build done, stale disk
        // already read) but before the pump executes the swap is provably inside the window.
        WriteUserFile(new() { ["hello"] = 120, ["zeppelin"] = 5 });
        var swaps = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        using var factory = new PredictionStackFactory(swaps.Enqueue);

        Action pump = () => { if (swaps.TryDequeue(out var run)) run(); };

        // Boot: first background build, pumped, puts a live stack in place.
        factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        await PumpUntilNonEmptyAsync(swaps);
        pump();
        Check.That(factory.CurrentWordPredictor).IsNotNull();

        // A second build goes in flight; when its continuation has queued the swap, the
        // build has finished and loaded whatever was on disk at its head — the pre-forget file.
        factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        await PumpUntilNonEmptyAsync(swaps);
        factory.ForgetWords(["zeppelin"]); // INSIDE the window: swap queued, not yet run
        pump();

        Check.That(factory.CurrentWordPredictor!.GetSuggestions("zep")
            .Select(w => w.Word)).Not.Contains("zeppelin");

        // And a deliberate re-learn survives a LATER build's swap (stamps expire, finding 2).
        factory.CurrentWordPredictor.LearnWord("zeppelin");
        factory.Rebuild(_settings, _baseDir, _appDataDir, modelRegistry: null);
        await PumpUntilNonEmptyAsync(swaps);
        pump();
        Check.That(factory.CurrentWordPredictor.GetSuggestions("zep")
            .Select(w => w.Word)).Contains("zeppelin");
    }

    private static async Task PumpUntilNonEmptyAsync(
        System.Collections.Concurrent.ConcurrentQueue<Action> swaps)
    {
        // The build task is tiny; poll briefly for the continuation to queue its swap. A fixed
        // delay would flake on a loaded runner — the repo's WaitFor-poll convention instead.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (swaps.IsEmpty)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The background build never queued its swap.");
            await Task.Delay(10);
        }
    }

    private sealed class HelloProvider : IDictionaryProvider
    {
        private readonly Dictionary<string, int> _words = new() { { "hello", 100 } };
        public Dictionary<string, int> Load() => new(_words);
        public void Save(Dictionary<string, int> words) { }
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
    }
}
