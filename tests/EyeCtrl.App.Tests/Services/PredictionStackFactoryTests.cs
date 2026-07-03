namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Settings;

public class PredictionStackFactoryTests : IDisposable
{
    private readonly string _appDataDir;
    private readonly string _baseDir;

    public PredictionStackFactoryTests()
    {
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl-psf-" + Guid.NewGuid().ToString("N"));
        _baseDir = Path.Combine(_appDataDir, "base");
        Directory.CreateDirectory(_appDataDir);
        Directory.CreateDirectory(_baseDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
    }

    private static EyeCtrlSettings PredictionSettings(bool enabled = true)
    {
        var s = new EyeCtrlSettings();
        s.Keyboard.PredictionEnabled = enabled;
        s.Keyboard.LanguagePack = "en";
        s.Keyboard.MLSuggestionsEnabled = false;
        return s;
    }

    [Fact]
    public void Rebuild_PredictionDisabled_ReturnsNull()
    {
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(PredictionSettings(enabled: false), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(orchestrator).IsNull();
        Check.That(factory.Current).IsNull();
    }

    [Fact]
    public void Rebuild_PredictionEnabled_ReturnsOrchestratorWithWordPredictor()
    {
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(PredictionSettings(), _baseDir, _appDataDir, modelRegistry: null);
        Check.That(orchestrator).IsNotNull();
        Check.That(orchestrator!.WordPredictor).IsNotNull();
        Check.That(factory.Current).IsSameReferenceAs(orchestrator);
    }

    [Fact]
    public void Rebuild_SavesLearnedWordsOfPreviousStack()
    {
        using var factory = new PredictionStackFactory();
        var settings = PredictionSettings();

        var first = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);
        first!.LearnWord("zebrastripes");
        first.LearnWord("zebrastripes");
        first.LearnWord("zebrastripes");

        var second = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);

        var words = second!.WordPredictor.GetSuggestions("zebrastr", null, 5).Select(r => r.Word).ToList();
        Check.That(words).Contains("zebrastripes");
    }

    [Fact]
    public void Dispose_SavesUserDictionaryToDisk()
    {
        var settings = PredictionSettings();
        using (var factory = new PredictionStackFactory())
        {
            var orchestrator = factory.Rebuild(settings, _baseDir, _appDataDir, modelRegistry: null);
            orchestrator!.LearnWord("qwertyzonk");
        }
        Check.That(File.Exists(Path.Combine(_appDataDir, "user_dictionary_en.json"))).IsTrue();
    }
}
