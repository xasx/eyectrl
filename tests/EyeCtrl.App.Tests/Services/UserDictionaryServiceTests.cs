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
        WriteUserFile(new() { ["hello"] = 120, ["zeppelin"] = 5, ["aardvark"] = 9 });
        var service = Create();

        // "hello" is base vocabulary that merely got used (its merged presence is frequency
        // inflation, not a dictionary entry the user added); the two novel words are learned.
        Check.That(service.GetLearnedWords()).ContainsExactly("aardvark", "zeppelin");
    }

    [Fact]
    public void GetLearnedWords_NoUserFile_IsEmpty()
    {
        Check.That(Create().GetLearnedWords()).IsEmpty();
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
        // pre-forget disk state must be removed from the freshly swapped-in stack too, and the
        // tombstone must die with that swap (a deliberate re-learn afterwards sticks).
        using var factory = new PredictionStackFactory();
        factory.ForgetWords(["zeppelin"]); // no live stack: tombstone only

        var predictor = new WordPredictor(new ThreeWordProvider());
        predictor.LearnWord("zeppelin");
        factory.ApplyPendingForgetsTo(predictor);
        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Not.Contains("zeppelin");

        predictor.LearnWord("zeppelin"); // re-learn AFTER the swap must not be tombstoned again
        factory.ApplyPendingForgetsTo(predictor);
        Check.That(predictor.GetSuggestions("zep").Select(s => s.Word)).Contains("zeppelin");
    }

    private sealed class ThreeWordProvider : IDictionaryProvider
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
