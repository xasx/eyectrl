namespace EyeCtrl.Core.Prediction;

using System.Text.Json.Serialization;

/// <summary>Deserialization DTO for the language model manifest JSON file.</summary>
public class ModelManifest
{
    /// <summary>Mapping from model size keys to their download details.</summary>
    [JsonPropertyName("models")]
    public Dictionary<string, ModelEntry> Models { get; set; } = [];
}

/// <summary>A single model entry in the manifest describing download location and integrity data.</summary>
public class ModelEntry
{
    /// <summary>Human-readable display name for the model.</summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = "";

    /// <summary>Download URL for the ONNX model file.</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    /// <summary>SHA-256 hash of the ONNX model file.</summary>
    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    /// <summary>Size of the ONNX model file in bytes.</summary>
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    /// <summary>Download URL for the tokenizer file.</summary>
    [JsonPropertyName("tokenizerUrl")]
    public string TokenizerUrl { get; set; } = "";

    /// <summary>SHA-256 hash of the tokenizer file.</summary>
    [JsonPropertyName("tokenizerSha256")]
    public string TokenizerSha256 { get; set; } = "";
}
