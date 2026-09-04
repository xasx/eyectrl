namespace EyeCtrl.Core.Tests.Adaptation;

using EyeCtrl.Core.Adaptation;

public class AdaptationStateFileTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "eyectrl_state_test_" + Guid.NewGuid() + ".json");

    [Fact]
    public void RoundTrip_PreservesValues()
    {
        string path = TempPath();
        try
        {
            var saved = new AdaptationState(
                AdaptationState.CurrentVersion, DateTimeOffset.UtcNow,
                new Dictionary<AdaptationKnob, double> { [AdaptationKnob.DwellTime] = 0.25 });
            AdaptationStateFile.Save(path, saved);

            var loaded = AdaptationStateFile.Load(path);
            Check.That(loaded.Version).IsEqualTo(AdaptationState.CurrentVersion);
            Check.That(loaded.Learned[AdaptationKnob.DwellTime]).IsCloseTo(0.25, 1e-9);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        var loaded = AdaptationStateFile.Load(TempPath()); // never written
        Check.That(loaded.Learned).IsEmpty();
    }

    [Fact]
    public void CorruptJson_LoadsEmpty_NeverThrows()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            Check.That(AdaptationStateFile.Load(path).Learned).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OutOfRangeEntry_IsDropped_OthersSurvive()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 1, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": 0.3, "fixationRadius": 7.0 } }
                """);
            var loaded = AdaptationStateFile.Load(path);
            Check.That(loaded.Learned.ContainsKey(AdaptationKnob.DwellTime)).IsTrue();
            Check.That(loaded.Learned.ContainsKey(AdaptationKnob.FixationRadius)).IsFalse();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void UnknownKnobName_IsIgnored()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 1, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": 0.3, "bogus": 0.4 } }
                """);
            var loaded = AdaptationStateFile.Load(path);
            Check.That(loaded.Learned).HasSize(1);
            Check.That(loaded.Learned.ContainsKey(AdaptationKnob.DwellTime)).IsTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void WrongVersion_LoadsEmpty()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 99, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": 0.3 } }
                """);
            Check.That(AdaptationStateFile.Load(path).Learned).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NegativeEntry_IsDropped()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path,
                """
                { "version": 1, "savedAtUtc": "2026-09-04T00:00:00Z",
                  "learned": { "dwellTime": -0.2 } }
                """);
            Check.That(AdaptationStateFile.Load(path).Learned).IsEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_IsAtomic_CreatesDirectory_LeavesNoTemp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "eyectrl_state_test_" + Guid.NewGuid());
        string path = Path.Combine(dir, "sub", "adaptation-state.json");
        try
        {
            AdaptationStateFile.Save(path, AdaptationState.Empty);
            Check.That(File.Exists(path)).IsTrue();
            Check.That(File.Exists(path + ".tmp")).IsFalse();
        }
        finally { Directory.Delete(dir, recursive: true); } // the GUID dir itself — NOT its parent (= %TEMP%)
    }
}
