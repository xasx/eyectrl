namespace EyeCtrl.Core.Prediction;

public enum ModelSource
{
    Bundled,
    Custom
}

public class ModelRegistryEntry
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Url { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public string TokenizerUrl { get; set; } = "";
    public string TokenizerSha256 { get; set; } = "";
    public ModelSource Source { get; set; }

    public bool IsDownloaded { get; set; }
    public bool IsActive { get; set; }
}
