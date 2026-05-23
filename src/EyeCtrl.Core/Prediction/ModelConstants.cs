namespace EyeCtrl.Core.Prediction;

public static class ModelConstants
{
    public static readonly IReadOnlyDictionary<string, string> ModelFileNames = new Dictionary<string, string>
    {
        ["135M"] = "smollm2-135m.onnx",
        ["360M"] = "smollm2-360m.onnx",
        ["1.5B"] = "qwen2.5-1.5b.onnx",
    };
}
