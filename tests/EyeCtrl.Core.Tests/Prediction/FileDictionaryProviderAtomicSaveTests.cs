namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using System.IO;

/// <summary>
/// Review-01 R4: the user dictionary was the one save the exit watchdog could corrupt — it was a
/// direct <c>File.WriteAllText</c>, so a FailFast kill mid-write truncated the owner's learned words.
/// <see cref="FileDictionaryProvider.Save(Dictionary{string, int})"/> now stages the JSON in a temp
/// file and swaps it in (the same pattern <c>SaveJson</c> already used), so a write that cannot
/// complete leaves the previous file whole.
/// </summary>
public class FileDictionaryProviderAtomicSaveTests : IDisposable
{
    private readonly string _dir;

    public FileDictionaryProviderAtomicSaveTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"dict_atomic_{Guid.NewGuid()}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    [Fact]
    public void Save_WhenTheTempFileCannotBeWritten_LeavesTheExistingDictionaryIntact()
    {
        // Arrange: a saved dictionary, and a directory squatting on the staging path so the temp
        // write fails before the destination is ever opened for writing.
        string userPath = Path.Combine(_dir, "user_dictionary_en.json");
        const string oldJson = "{\"old\":3}";
        File.WriteAllText(userPath, oldJson);
        Directory.CreateDirectory(userPath + ".tmp");

        var provider = new FileDictionaryProvider(Path.Combine(_dir, "base.json"), _dir, "en");

        // Act
        Exception? failure = null;
        try
        {
            provider.Save(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["neu"] = 2 });
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        // Assert: the save reports the failure instead of silently overwriting through it, and the
        // file the owner already had is untouched. A direct WriteAllText replaces it here.
        Check.WithCustomMessage("the save must fail when its temp file cannot be created")
            .That(failure).IsNotNull();
        Check.WithCustomMessage("a failed save must leave the previous dictionary whole")
            .That(File.ReadAllText(userPath)).IsEqualTo(oldJson);
    }
}
