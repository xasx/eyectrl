namespace EyeCtrl.App.Services;

using System.IO;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.LM;
using Serilog;

public class PredictionStackFactory : IDisposable
{
    private PredictionOrchestrator? _current;
    private bool _disposed;

    public PredictionOrchestrator? Current => _current;

    public PredictionOrchestrator? Rebuild(
        EyeCtrlSettings settings,
        string baseDir,
        string appDataDir,
        ModelRegistryService? modelRegistry)
    {
        SaveAndDisposeCurrent();

        if (!settings.Keyboard.PredictionEnabled)
        {
            _current = null;
            return null;
        }

        var langCode = settings.Keyboard.LanguagePack;
        var dictPath = Path.Combine(baseDir, "Resources", "Dictionaries", $"{langCode}.json");
        var provider = new FileDictionaryProvider(dictPath, appDataDir, langCode);
        var wordPredictor = new WordPredictor(provider, settings.Keyboard.ActiveLayout);
        wordPredictor.ApplyDecay(settings.Keyboard.DecayEnabled, settings.Keyboard.DecayHalfLifeDays);

        SentenceCompletionIndex? trieIndex = null;
        var triePath = Path.Combine(baseDir, "Resources", "Completions", $"completions_{langCode}.bin");
        if (File.Exists(triePath))
        {
            try
            {
                using var trieStream = File.OpenRead(triePath);
                trieIndex = SentenceCompletionIndex.Load(trieStream);
                Log.Information("Loaded sentence completion trie: {Path}", triePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load sentence completion trie from {Path}", triePath);
            }
        }

        ILanguageModelPredictor? lmPredictor = null;
        if (settings.Keyboard.MLSuggestionsEnabled && modelRegistry != null)
        {
            var modelsDir = Path.Combine(appDataDir, "models");
            var modelSize = settings.Keyboard.MLModelSize;

            if (!string.IsNullOrEmpty(modelSize) && modelSize != "Off")
            {
                LMTokenizer? tokenizer = null;
                var tokenizerPath = modelRegistry.GetTokenizerPath(modelSize);

                try
                {
                    if (File.Exists(tokenizerPath))
                        tokenizer = new LMTokenizer(tokenizerPath);
                    else
                        Log.Warning("Tokenizer file not found at {Path}; LM suggestions disabled", tokenizerPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to load tokenizer from {Path}; LM suggestions disabled", tokenizerPath);
                }

                var modelPath = modelRegistry.GetModelPath(modelSize);

                lmPredictor = new SmallLMPredictor(
                    modelsDir,
                    modelSize,
                    settings.Keyboard.PreferGPU,
                    tokenizer: tokenizer,
                    modelPath: modelPath);
            }
        }

        _current = new PredictionOrchestrator(wordPredictor, trieIndex, lmPredictor, settings.Keyboard.MLSuggestionsEnabled);
        return _current;
    }

    public void SaveAndDisposeCurrent()
    {
        if (_current != null)
        {
            // Save must not prevent disposal: a failed dictionary write should still let the old
            // orchestrator (and its LM predictor) be disposed, otherwise a throwing save leaks it.
            try
            {
                _current.WordPredictor.SaveUserDictionary();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to save user dictionary before prediction-stack rebuild");
            }
            _current.Dispose();
            _current = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SaveAndDisposeCurrent();
    }
}
