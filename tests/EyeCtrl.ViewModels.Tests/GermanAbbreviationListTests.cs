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

    private WordPredictor CreatePredictor()
        => new(new FileDictionaryProvider(_baseDictionaryPath, _appDataDir, "de"));

    private (KeyboardViewModel Vm, WordPredictor Predictor) Create()
    {
        var settings = new EyeCtrlSettings { Keyboard = { SmartCapitalizationEnabled = false } };
        var predictor = CreatePredictor();
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

    [Theory]
    [InlineData("AG.", "ag")]
    [InlineData("GmbH.", "gmbh")]
    [InlineData("kg.", "kg")]
    public void UnitAndCompanyAbbreviations_LearnWithoutThePeriod_OnBothPaths(
        string raw, string expectedKey)
    {
        // AG, GmbH and kg are written without a period, so the period is a sentence end.
        var (spaceVm, spacePredictor) = Create();
        using (spaceVm)
        {
            CommitWithSpace(spaceVm, raw);

            Check.WithCustomMessage("Space kept the sentence period on a no-period abbreviation")
                .That(spacePredictor.WordFrequencies.ContainsKey(expectedKey)).IsTrue();
        }

        var (addVm, addPredictor) = Create();
        using (addVm)
        {
            CommitWithAddWord(addVm, raw);

            Check.WithCustomMessage("Add Word kept the sentence period on a no-period abbreviation")
                .That(addPredictor.WordFrequencies.ContainsKey(expectedKey)).IsTrue();
        }
    }

    [Fact]
    public void NameAndCompanySentenceEnds_ForceTheNextCapital()
    {
        var predictor = CreatePredictor();

        Check.WithCustomMessage("a name's period was treated as an abbreviation")
            .That(CapitalizationHelper.ShouldCapitalize("Ich heiße Max.", predictor.Abbreviations))
            .IsTrue();
        Check.WithCustomMessage("AG. is a sentence end and must start a new sentence")
            .That(CapitalizationHelper.ShouldCapitalize(
                "Das ist die Siemens AG.", predictor.Abbreviations)).IsTrue();
    }

    [Fact]
    public void RealAbbreviation_DoesNotForceTheNextCapital()
    {
        var predictor = CreatePredictor();

        Check.WithCustomMessage("usw. is an abbreviation and must not force a capital")
            .That(CapitalizationHelper.ShouldCapitalize(
                "Das ist usw.", predictor.Abbreviations)).IsFalse();
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
