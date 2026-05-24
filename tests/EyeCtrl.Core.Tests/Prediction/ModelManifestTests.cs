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
}
