namespace EyeCtrl.Benchmarks;

internal static class ModelPathHelper
{
    internal static string GetModelDirectory()
    {
        var envDir = Environment.GetEnvironmentVariable("EYECTRL_MODEL_DIR");
        return envDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EyeCtrl", "models");
    }

    internal static (string[] onnxFiles, string[] tokenizerFiles) FindModelFiles(string modelDir)
    {
        if (!Directory.Exists(modelDir))
            return ([], []);

        var onnxFiles = Directory.GetFiles(modelDir, "*.onnx", SearchOption.TopDirectoryOnly);
        var tokenizerFiles = Directory.GetFiles(modelDir, "*.tokenizer.json", SearchOption.TopDirectoryOnly);

        return (onnxFiles, tokenizerFiles);
    }
}
