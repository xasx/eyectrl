namespace EyeCtrl.ViewModels.Tests;

using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.ViewModels;

/// <summary>
/// The SHIPPED German abbreviation list, as the learn trim reads it (<c>de.json</c>), not a
/// fixture: an abbreviation keeps its period when committed with Space or Add Word, while a
/// normal sentence-final word (and the owner's name example, "Max.") loses it.
/// </summary>
public class GermanAbbreviationListTests : IDisposable
{
    /// <summary>The entries the owner approved adding (2026-10-21).</summary>
    private static readonly string[] AddedEntries =
    [
        "bsp.", "bspw.", "vgl.", "bzgl.", "zzgl.", "usf.", "uvm.", "mwst.",
        "hrsg.", "abb.", "anm.", "aufl.", "kap.", "allg.", "betr.", "urspr.",
        "engl.", "frz.",
    ];

    private readonly string _appDataDir;
    private readonly string _baseDictionaryPath = ShippedGermanDictionary();

    public GermanAbbreviationListTests()
    {
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl_de_abbr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_appDataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_appDataDir)) Directory.Delete(_appDataDir, true);
    }

    public static TheoryData<string> AddedEntryRows
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var entry in AddedEntries) data.Add(entry);
            return data;
        }
    }

    private static string ShippedGermanDictionary()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName,
                "src", "EyeCtrl.App", "Resources", "Dictionaries", "de.json");
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException(
            "de.json was not found above " + AppContext.BaseDirectory);
    }

    private (KeyboardViewModel Vm, WordPredictor Predictor) Create()
    {
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        var predictor = new WordPredictor(
            new FileDictionaryProvider(_baseDictionaryPath, _appDataDir, "de"));
        var orchestrator = new PredictionOrchestrator(predictor);
        var vm = new KeyboardViewModel(settings, new WeakReferenceMessenger());
        vm.SetOrchestrator(orchestrator);
        return (vm, predictor);
    }

    private static void Type(KeyboardViewModel vm, string text)
    {
        foreach (var ch in text) vm.ActivateKey(ch.ToString(), null);
    }

    private static void CommitWithSpace(KeyboardViewModel vm, string word)
    {
        Type(vm, word);
        vm.HandleSpace();
    }

    private static void CommitWithAddWord(KeyboardViewModel vm, string word)
    {
        vm.CurrentInput = word;
        vm.HandleAddWord();
    }

    [Fact]
    public void OwnerNameMax_LearnsWithoutThePeriod_OnBothPaths()
    {
        var (spaceVm, spacePredictor) = Create();
        using (spaceVm)
        {
            CommitWithSpace(spaceVm, "Max.");

            Check.WithCustomMessage("Space kept the period on a normal name")
                .That(spacePredictor.WordFrequencies.ContainsKey("max")).IsTrue();
            Check.That(spacePredictor.WordFrequencies.ContainsKey("max.")).IsFalse();
        }

        var (addVm, addPredictor) = Create();
        using (addVm)
        {
            CommitWithAddWord(addVm, "Max.");

            Check.WithCustomMessage("Add Word kept the period on a normal name")
                .That(addPredictor.WordFrequencies.ContainsKey("max")).IsTrue();
            Check.That(addPredictor.WordFrequencies.ContainsKey("max.")).IsFalse();
        }
    }

    [Theory]
    [MemberData(nameof(AddedEntryRows))]
    public void AddedAbbreviation_KeepsItsPeriod_OnBothPaths(string abbreviation)
    {
        var (spaceVm, spacePredictor) = Create();
        using (spaceVm)
        {
            CommitWithSpace(spaceVm, abbreviation);

            Check.WithCustomMessage("Space dropped the period of a list abbreviation")
                .That(spacePredictor.WordFrequencies.ContainsKey(abbreviation)).IsTrue();
        }

        var (addVm, addPredictor) = Create();
        using (addVm)
        {
            CommitWithAddWord(addVm, abbreviation);

            Check.WithCustomMessage("Add Word dropped the period of a list abbreviation")
                .That(addPredictor.WordFrequencies.ContainsKey(abbreviation)).IsTrue();
        }
    }

    [Fact]
    public void NormalSentenceEnd_StillLosesThePeriod_OnBothPaths()
    {
        var (spaceVm, spacePredictor) = Create();
        using (spaceVm)
        {
            CommitWithSpace(spaceVm, "Ende.");

            Check.That(spacePredictor.WordFrequencies.ContainsKey("ende")).IsTrue();
            Check.That(spacePredictor.WordFrequencies.ContainsKey("ende.")).IsFalse();
        }

        var (addVm, addPredictor) = Create();
        using (addVm)
        {
            CommitWithAddWord(addVm, "Ende.");

            Check.That(addPredictor.WordFrequencies.ContainsKey("ende")).IsTrue();
            Check.That(addPredictor.WordFrequencies.ContainsKey("ende.")).IsFalse();
        }
    }
}
