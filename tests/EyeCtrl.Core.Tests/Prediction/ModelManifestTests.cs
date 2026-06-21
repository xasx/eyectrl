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

        Check.That(manifest).IsNotNull();
        Check.That(manifest!.Models).IsNotNull();
        Check.That(manifest.Models.Count).IsEqualTo(2);
        Check.That(manifest.Models["135M"].SizeBytes).IsEqualTo(73400320);
        Check.That(manifest.Models["135M"].Sha256).IsEqualTo("abc123def456");
        Check.That(manifest.Models["135M"].TokenizerSha256).IsEqualTo("tok456abc789");
        Check.That(manifest.Models["360M"].TokenizerSha256).IsEqualTo("tok789def012");
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
        Check.That(manifest).IsNotNull();
        var entry = manifest!.Models["135M"];
        Check.That(entry.DisplayName).IsEqualTo("SmolLM2 135M");
        Check.That(entry.TokenizerUrl).IsEqualTo("https://example.com/tokenizer.json");
        Check.That(entry.TokenizerSha256).IsEqualTo("def");
    }
}
