namespace EyeCtrl.Core.Prediction;

public static class ModelConstants
{
    public static IReadOnlyDictionary<string, string> ModelFileNames => _modelFileNames;

    private static readonly IReadOnlyDictionary<string, string> _modelFileNames = new Dictionary<string, string>
    {
        ["135M"] = "smollm2-135m.onnx",
        ["360M"] = "smollm2-360m.onnx",
        ["0.5B"] = "qwen2.5-0.5b.onnx",
        ["1.5B"] = "qwen2.5-1.5b.onnx",
        ["3.8B"] = "phi-3.5-mini.onnx",
    };
}
