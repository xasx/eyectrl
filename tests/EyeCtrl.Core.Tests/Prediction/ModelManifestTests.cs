namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;
using System.Text.Json;

public class ModelManifestTests
{
    [Fact]
    public void Deserialize_ValidJson_ReturnsModels()
    {
        var json = """
        {
            "models": {
                "135M": {
                    "url": "https://huggingface.co/org/model-135m/resolve/main/model.onnx",
                    "sha256": "abc123def456",
                    "sizeBytes": 73400320,
                    "tokenizerSha256": "tok456abc789"
                },
                "360M": {
                    "url": "https://huggingface.co/org/model-360m/resolve/main/model.onnx",
                    "sha256": "def789ghi012",
                    "sizeBytes": 188743680,
                    "tokenizerSha256": "tok789def012"
                }
            }
        }
        """;

        var manifest = JsonSerializer.Deserialize<ModelManifest>(json);

        Assert.NotNull(manifest);
        Assert.NotNull(manifest!.Models);
        Assert.Equal(2, manifest.Models.Count);
        Assert.Equal(73400320, manifest.Models["135M"].SizeBytes);
        Assert.Equal("abc123def456", manifest.Models["135M"].Sha256);
        Assert.Equal("tok456abc789", manifest.Models["135M"].TokenizerSha256);
        Assert.Equal("tok789def012", manifest.Models["360M"].TokenizerSha256);
    }

    [Fact]
    public void Deserialize_WithDisplayName_ParsesField()
    {
        var json = """
        {
            "models": {
                "135M": {
                    "displayName": "SmolLM2 135M",
                    "url": "https://example.com/model.onnx",
                    "sha256": "abc",
                    "sizeBytes": 70000000,
                    "tokenizerUrl": "https://example.com/tokenizer.json",
                    "tokenizerSha256": "def"
                }
            }
        }
        """;

        var manifest = JsonSerializer.Deserialize<ModelManifest>(json);
        Assert.NotNull(manifest);
        var entry = manifest!.Models["135M"];
        Assert.Equal("SmolLM2 135M", entry.DisplayName);
        Assert.Equal("https://example.com/tokenizer.json", entry.TokenizerUrl);
        Assert.Equal("def", entry.TokenizerSha256);
    }
}
