namespace EyeCtrl.Core.Tests.Keyboard;

using EyeCtrl.Core.Keyboard;

/// <summary>autoReturn rides the existing camelCase System.Text.Json binding (spec D5).</summary>
public class KeyboardLayoutAutoReturnTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eyectrl_autoreturn_" + Guid.NewGuid());

    public KeyboardLayoutAutoReturnTests() => Directory.CreateDirectory(_dir);
    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private KeyboardLayout SaveAndLoad(string json)
    {
        var path = Path.Combine(_dir, "layout.json");
        File.WriteAllText(path, json);
        return KeyboardLayoutLoader.Load(path);
    }

    [Fact]
    public void AutoReturn_LoadsTrue_AndDefaultsToFalse()
    {
        var layout = SaveAndLoad("""
        {
          "name": "T", "defaultSubLayout": "letters",
          "subLayouts": {
            "letters": { "label": "ABC", "rows": [ [] ] },
            "accents": { "label": "Acc", "autoReturn": true, "rows": [ [] ] }
          }
        }
        """);

        Check.That(layout.SubLayouts["accents"].AutoReturn).IsTrue();
        Check.That(layout.SubLayouts["letters"].AutoReturn).IsFalse();
    }

    [Fact]
    public void AutoReturn_SurvivesSaveRoundTrip()
    {
        var path = Path.Combine(_dir, "roundtrip.json");
        KeyboardLayoutLoader.Save(SaveAndLoad("""
        {
          "name": "T", "defaultSubLayout": "letters",
          "subLayouts": {
            "letters": { "label": "ABC", "rows": [ [] ] },
            "accents": { "label": "Acc", "autoReturn": true, "rows": [ [] ] }
          }
        }
        """), path);

        Check.That(KeyboardLayoutLoader.Load(path).SubLayouts["accents"].AutoReturn).IsTrue();
    }
}
