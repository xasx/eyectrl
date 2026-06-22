namespace EyeCtrl.Core.Prediction;

/// <summary>Source of a model entry: bundled with the app or user-defined custom model.</summary>
public enum ModelSource
{
    /// <summary>Model from the built-in manifest.</summary>
    Bundled,
    /// <summary>User-defined custom model.</summary>
    Custom
}

/// <summary>A single entry in the model registry tracking download status and activation state.</summary>
public class ModelRegistryEntry
{
    /// <summary>Unique model identifier (e.g., "360M").</summary>
    public string Id { get; set; } = "";
    /// <summary>Human-readable display name.</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Download URL for the ONNX model file.</summary>
    public string Url { get; set; } = "";
    /// <summary>SHA-256 hash for integrity verification.</summary>
    public string Sha256 { get; set; } = "";
    /// <summary>Size of the ONNX model file in bytes.</summary>
    public long SizeBytes { get; set; }
    /// <summary>Download URL for the tokenizer file.</summary>
    public string TokenizerUrl { get; set; } = "";
    /// <summary>SHA-256 hash of the tokenizer file.</summary>
    public string TokenizerSha256 { get; set; } = "";
    /// <summary>Whether this is a bundled or custom model.</summary>
    public ModelSource Source { get; set; }

    /// <summary>Whether both the ONNX model and tokenizer files exist on disk.</summary>
    public bool IsDownloaded { get; set; }
    /// <summary>Whether this model is currently the active prediction model.</summary>
    public bool IsActive { get; set; }
}
