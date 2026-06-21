namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;

public class ModelRegistryServiceTests
{
    [Fact]
    public void Constructor_MergesBundledAndCustomModels()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest
            {
                Models = new()
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/135m.onnx",
                        Sha256 = "abc",
                        SizeBytes = 70_000_000,
                        TokenizerUrl = "https://example.com/tokenizer.json",
                        TokenizerSha256 = "def"
                    }
                }
            };
            var settings = new EyeCtrlSettings();
            settings.Keyboard.CustomModels.Add(new CustomModelEntry
            {
                Id = "MyModel",
                DisplayName = "My Custom Model",
                Url = "https://example.com/custom.onnx",
                Sha256 = "xyz",
                SizeBytes = 100_000_000,
                TokenizerUrl = "https://example.com/custom_tokenizer.json",
                TokenizerSha256 = "uvw"
            });

            var service = new ModelRegistryService(tempDir, manifest, settings);

            Check.That(service.Registry.Count).IsEqualTo(2);
            Check.That(service.Registry.ContainsKey("135M")).IsTrue();
            Check.That(service.Registry.ContainsKey("MyModel")).IsTrue();
            Check.That(service.Registry["135M"].Source).IsEqualTo(ModelSource.Bundled);
            Check.That(service.Registry["MyModel"].Source).IsEqualTo(ModelSource.Custom);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelFileName_BundledModel_ReturnsMappedName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest
            {
                Models = new()
                {
                    ["135M"] = new ModelEntry { DisplayName = "SmolLM2 135M", Url = "", TokenizerUrl = "" }
                }
            };
            var service = new ModelRegistryService(tempDir, manifest, new EyeCtrlSettings());

            var fileName = service.GetModelFileName("135M");
            Check.That(fileName).IsEqualTo("smollm2-135m.onnx");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelFileName_CustomModel_ReturnsSanitizedName()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest();
            var settings = new EyeCtrlSettings();
            settings.Keyboard.CustomModels.Add(new CustomModelEntry
            {
                Id = "My Model! v2",
                DisplayName = "My Model",
                Url = "",
                TokenizerUrl = ""
            });

            var service = new ModelRegistryService(tempDir, manifest, settings);
            var fileName = service.GetModelFileName("My Model! v2");
            Check.That(fileName).IsEqualTo("My_Model_v2.onnx");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SanitizeFileName_InvalidChars_ReplacesWithUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("Hello! World? (test)");
        Check.That(result).IsEqualTo("Hello_World_test");
    }

    [Fact]
    public void SanitizeFileName_CollapsesMultipleUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("foo!!!bar");
        Check.That(result).IsEqualTo("foo_bar");
    }

    [Fact]
    public void SanitizeFileName_TrimsLeadingTrailingUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("!hello world!");
        Check.That(result).IsEqualTo("hello_world");
    }

    [Fact]
    public void SanitizeFileName_EmptyInput_ReturnsFallback()
    {
        var result = ModelRegistryService.SanitizeFileName("!!!");
        Check.That(result).IsEqualTo("custom_model");
    }

    [Fact]
    public void SanitizeFileName_MaxLengthEnforced()
    {
        var longName = new string('a', 128);
        var result = ModelRegistryService.SanitizeFileName(longName);
        Check.That(result.Length <= 64).IsTrue();
    }

    [Fact]
    public void AddCustomModel_AddsToRegistryAndSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest();
            var settings = new EyeCtrlSettings();
            var service = new ModelRegistryService(tempDir, manifest, settings);

            service.AddCustomModel("NewModel", "New Model", "https://example.com/model.onnx",
                "hash", 50000000, "https://example.com/tokenizer.json", "tokenizerHash", settings.Keyboard);

            Check.That(service.Registry.ContainsKey("NewModel")).IsTrue();
            Check.That(settings.Keyboard.CustomModels).HasSize(1);
            Check.That(settings.Keyboard.CustomModels[0].DisplayName).IsEqualTo("New Model");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RemoveCustomModel_RemovesFromRegistryAndSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"reg_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var manifest = new ModelManifest();
            var settings = new EyeCtrlSettings();
            settings.Keyboard.CustomModels.Add(new CustomModelEntry
            {
                Id = "ToDelete",
                DisplayName = "Delete Me",
                Url = "",
                TokenizerUrl = ""
            });

            var service = new ModelRegistryService(tempDir, manifest, settings);
            Check.That(service.Registry.ContainsKey("ToDelete")).IsTrue();

            service.RemoveCustomModel("ToDelete", settings.Keyboard);
            Check.That(service.Registry.ContainsKey("ToDelete")).IsFalse();
            Check.That(settings.Keyboard.CustomModels).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
