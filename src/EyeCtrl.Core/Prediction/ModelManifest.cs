namespace EyeCtrl.Core.Prediction;

using System.Text.Json.Serialization;

public class ModelManifest
{
    [JsonPropertyName("models")]
    public Dictionary<string, ModelEntry> Models { get; set; } = [];
}

public class ModelEntry
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = "";

    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("tokenizerSha256")]
    public string TokenizerSha256 { get; set; } = "";
}
