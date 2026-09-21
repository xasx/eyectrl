namespace EyeCtrl.App.Tests.Settings.Pages;

using System.IO;
using System.Text.Json;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.App.Views.Settings;
using EyeCtrl.Core.Settings;

/// <summary>
/// The Keyboard tab's learned-words page (prediction controls #22). Built only when the context
/// carries the dictionary service; the QA delete-row pattern with confirm + rebuild.
/// </summary>
public class KeyboardDictionaryPageTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _appDataDir;

    public KeyboardDictionaryPageTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "eyectrl_kbp_" + Guid.NewGuid());
        _appDataDir = Path.Combine(Path.GetTempPath(), "eyectrl_kbp_app_" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(_baseDir, "Resources", "Dictionaries"));
        Directory.CreateDirectory(_appDataDir);
        File.WriteAllText(
            Path.Combine(_baseDir, "Resources", "Dictionaries", "en.json"),
            JsonSerializer.Serialize(new { words = new Dictionary<string, int> { ["hello"] = 100 } }));
        File.WriteAllText(
            Path.Combine(_appDataDir, "user_dictionary_en.json"),
            JsonSerializer.Serialize(new Dictionary<string, int> { ["hello"] = 100, ["zeppelin"] = 5, ["aardvark"] = 9 }));
    }

    private UserDictionaryService CreateService()
    {
        var settings = new EyeCtrlSettings();
        settings.Keyboard.LanguagePack = "en";
        return new UserDictionaryService(settings, _appDataDir, _baseDir);
    }

    [Fact]
    public void Build_WithoutTheService_KeepsTheSingleKeyboardPage()
    {
        var pages = new KeyboardPageSource(TestSettingsPageContext.Build()).Build();
        Check.That(pages).HasSize(1);
    }

    [Fact]
    public void Build_WithLearnedWords_AddsADictionaryPageWithDeleteRows()
    {
        var ctx = TestSettingsPageContext.Build(dictionary: CreateService());
        var pages = new KeyboardPageSource(ctx).Build();
        Check.That(pages).HasSize(2);

        var rows = pages[1].Rows;
        var deletes = rows.OfType<ButtonSpec>().Where(b => b.Id.StartsWith("kb_wd_del_")).ToList();
        Check.That(deletes.Select(b => b.Id)).ContainsExactly("kb_wd_del_aardvark", "kb_wd_del_zeppelin");
        Check.That(rows.OfType<ButtonSpec>().Select(b => b.Id)).Contains("kb_wd_clear");
        // A word label precedes each delete (the QA en/del pairing without the toggle). The
        // label wraps the word in typographic quotes — straight-quote ASCII would read as a
        // keycap at settings scale.
        Check.That(rows.OfType<LabelSpec>().Select(l => l.Text())).Contains("\u201Caardvark\u201D", "\u201Czeppelin\u201D");
    }

    [Fact]
    public void DeleteWord_Confirmed_ReducesTheLearnedList()
    {
        var service = CreateService();
        var ctx = TestSettingsPageContext.Build(dictionary: service, showModal: (_, act) => act());
        var pages = new KeyboardPageSource(ctx).Build();

        pages[1].Rows.OfType<ButtonSpec>()
            .Single(b => b.Id == "kb_wd_del_zeppelin").OnActivate();

        Check.That(service.GetLearnedWords()).ContainsExactly("aardvark");
    }

    [Fact]
    public void ClearAll_Confirmed_EmptiesThePage()
    {
        var service = CreateService();
        var ctx = TestSettingsPageContext.Build(dictionary: service, showModal: (_, act) => act());
        var pages = new KeyboardPageSource(ctx).Build();

        pages[1].Rows.OfType<ButtonSpec>()
            .Single(b => b.Id == "kb_wd_clear").OnActivate();
        var rebuilt = new KeyboardPageSource(ctx).Build();

        Check.That(service.GetLearnedWords()).IsEmpty();
        // The rebuilt page shows the empty-state hint instead of rows.
        Check.That(rebuilt[1].Rows.OfType<ButtonSpec>()).IsEmpty();
        Check.That(rebuilt[1].Rows[0]).IsInstanceOf<LabelSpec>();
    }

    [Fact]
    public void DeleteWord_LiveOnlyAddWordLearn_NoErrorDialog()
    {
        // Round-2 N2-A through the page: a never-persisted learn deletes cleanly — no ShowError,
        // the rebuild shows it gone.
        using var factory = new PredictionStackFactory();
        var orchestrator = factory.Rebuild(
            new EyeCtrlSettings { Keyboard = new KeyboardSettings { LanguagePack = "en" } },
            _baseDir, _appDataDir, modelRegistry: null);
        orchestrator!.WordPredictor.LearnWord("zeppelin");

        var service = CreateService();
        service.SetFactory(factory);
        var recorder = new RecordingSettingsPageContext(dictionary: service);
        var pages = new KeyboardPageSource(recorder.Context).Build();

        pages[1].Rows.OfType<ButtonSpec>()
            .Single(b => b.Id == "kb_wd_del_zeppelin").OnActivate();
        recorder.LastModalOnYes!(); // the recorder records the modal; the test confirms it

        Check.That(recorder.Errors).IsEmpty();
        // zeppelin (the live-only learn) is gone; aardvark from the file legitimately remains.
        Check.That(service.GetLearnedWords()).ContainsExactly("aardvark");
    }

    [Fact]
    public void DeleteWord_ReadOnlyFile_ShowsError()
    {
        // Round-2 N2-B through the page: a real write failure surfaces as ShowError, and the
        // throw never escapes the dwell-activated callback.
        var service = CreateService();
        var path = Path.Combine(_appDataDir, "user_dictionary_en.json");
        var attributes = File.GetAttributes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        RecordingSettingsPageContext recorder;
        try
        {
            recorder = new RecordingSettingsPageContext(dictionary: service);
            var pages = new KeyboardPageSource(recorder.Context).Build();
            pages[1].Rows.OfType<ButtonSpec>()
                .Single(b => b.Id == "kb_wd_del_zeppelin").OnActivate();
            recorder.LastModalOnYes!();
        }
        finally
        {
            File.SetAttributes(path, attributes);
        }

        Check.That(recorder.Errors).HasSize(1);
    }

    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); } catch (IOException) { }
        try { Directory.Delete(_appDataDir, recursive: true); } catch (IOException) { }
    }
}
