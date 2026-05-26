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

            Assert.Equal(2, service.Registry.Count);
            Assert.True(service.Registry.ContainsKey("135M"));
            Assert.True(service.Registry.ContainsKey("MyModel"));
            Assert.Equal(ModelSource.Bundled, service.Registry["135M"].Source);
            Assert.Equal(ModelSource.Custom, service.Registry["MyModel"].Source);
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
            Assert.Equal("smollm2-135m.onnx", fileName);
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
            Assert.Equal("My_Model_v2.onnx", fileName);
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
        Assert.Equal("Hello_World_test", result);
    }

    [Fact]
    public void SanitizeFileName_CollapsesMultipleUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("foo!!!bar");
        Assert.Equal("foo_bar", result);
    }

    [Fact]
    public void SanitizeFileName_TrimsLeadingTrailingUnderscores()
    {
        var result = ModelRegistryService.SanitizeFileName("!hello world!");
        Assert.Equal("hello_world", result);
    }

    [Fact]
    public void SanitizeFileName_EmptyInput_ReturnsFallback()
    {
        var result = ModelRegistryService.SanitizeFileName("!!!");
        Assert.Equal("custom_model", result);
    }

    [Fact]
    public void SanitizeFileName_MaxLengthEnforced()
    {
        var longName = new string('a', 128);
        var result = ModelRegistryService.SanitizeFileName(longName);
        Assert.True(result.Length <= 64);
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

            Assert.True(service.Registry.ContainsKey("NewModel"));
            Assert.Single(settings.Keyboard.CustomModels);
            Assert.Equal("New Model", settings.Keyboard.CustomModels[0].DisplayName);
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
            Assert.True(service.Registry.ContainsKey("ToDelete"));

            service.RemoveCustomModel("ToDelete", settings.Keyboard);
            Assert.False(service.Registry.ContainsKey("ToDelete"));
            Assert.Empty(settings.Keyboard.CustomModels);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
