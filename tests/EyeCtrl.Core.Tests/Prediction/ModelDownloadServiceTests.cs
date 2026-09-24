namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class ModelDownloadServiceTests
{
    [Fact]
    public void InstallDownloadedFile_ReplaceWinsAgainstAnOpenReader_MidHashShape()
    {
        // Issue #166 round-1 I1, deterministic (single thread — no timing): File.Move(
        // overwrite: true) fails with UnauthorizedAccessException whenever the destination has
        // an open handle, whatever the opener's share mode — measured. A repair re-download
        // hits exactly that: a background build's VerifyFileSha256 stream is open on the model
        // file mid-hash when the install runs. The stream here opens with the same
        // FileShare.Read|FileShare.Delete VerifyFileSha256 uses.
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dst = Path.Combine(tempDir, "model.onnx");
            var temp = dst + ".download";
            File.WriteAllText(dst, "old-model-bytes-old-model-bytes");
            File.WriteAllText(temp, "new-model-bytes-new-model-bytes");

            using var reader = new FileStream(dst, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            using var buffered = new System.IO.BufferedStream(reader);
            var oldBytes = new byte[8];
            buffered.ReadExactly(oldBytes); // position mid-file, like a hash mid-read

            ModelDownloadService.InstallDownloadedFile(temp, dst);

            Check.That(File.ReadAllText(dst)).IsEqualTo("new-model-bytes-new-model-bytes");
            // The open reader keeps serving the pre-replace content until it reopens —
            // File.Replace's POSIX-style semantics, and why nothing has to coordinate the
            // hash with the install.
            buffered.Position = 0;
            var reread = new byte[8];
            buffered.ReadExactly(reread);
            Check.That(System.Text.Encoding.UTF8.GetString(reread)).IsEqualTo("old-mode");
            Check.That(File.Exists(temp)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void InstallDownloadedFile_FirstInstall_MovesTempIntoPlace()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dst = Path.Combine(tempDir, "model.onnx");
            var temp = dst + ".download";
            File.WriteAllText(temp, "first-download");

            ModelDownloadService.InstallDownloadedFile(temp, dst);

            Check.That(File.ReadAllText(dst)).IsEqualTo("first-download");
            Check.That(File.Exists(temp)).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_ExistingFile_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.tokenizer.json"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Check.That(service.IsModelDownloaded("1.5B")).IsTrue();
            Check.That(service.IsModelDownloaded("360M")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetModelPath_ReturnsFullPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ModelDownloadService(tempDir);
            var path = service.GetModelPath("360M");

            Check.That(path).EndsWith("smollm2-360m.onnx");
            Check.That(path).StartsWith(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetTokenizerPath_ReturnsFullPath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var service = new ModelDownloadService(tempDir);
            var path = service.GetTokenizerPath("360M");

            Check.That(path).EndsWith("smollm2-360m.tokenizer.json");
            Check.That(path).StartsWith(tempDir);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_WithOnnxButNoTokenizer_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Check.That(service.IsModelDownloaded("135M")).IsFalse();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void IsModelDownloaded_WithOnnxAndTokenizer_ReturnsTrue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "qwen2.5-1.5b.tokenizer.json"), "dummy");
            var service = new ModelDownloadService(tempDir);

            Check.That(service.IsModelDownloaded("1.5B")).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetTokenizerUrl_StandardOnnxUrl_ReplacesLastSegment()
    {
        var url = "https://huggingface.co/org/repo/resolve/main/onnx/model.onnx";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://huggingface.co/org/repo/resolve/main/onnx/tokenizer.json");
    }

    [Fact]
    public void GetTokenizerUrl_UrlWithQueryParams_StripsThem()
    {
        var url = "https://huggingface.co/org/repo/resolve/main/model.onnx?download=true";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://huggingface.co/org/repo/resolve/main/tokenizer.json");
    }

    [Fact]
    public void GetTokenizerUrl_EmptyUrl_Throws()
    {
        Check.ThatCode(() => ModelDownloadService.GetTokenizerUrl("")).Throws<ArgumentException>();
    }

    [Fact]
    public void GetTokenizerUrl_RootPath_ReturnsTokenizer()
    {
        var url = "https://example.com/model.onnx";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://example.com/tokenizer.json");
    }

    [Fact]
    public void GetTokenizerUrl_NestedPath_ReplacesLastComponent()
    {
        var url = "https://example.com/a/b/c/model.onnx";
        var result = ModelDownloadService.GetTokenizerUrl(url);
        Check.That(result).IsEqualTo("https://example.com/a/b/c/tokenizer.json");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request, ct));
    }

    [Fact]
    public async Task DownloadModelAsync_ValidPayloadAndHash_WritesFileAndReportsCompletion()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("hello model payload");
            var expectedSha = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(payload));
            using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                }));
            using var service = new ModelDownloadService(tempDir, client);
            var events = new List<ModelDownloadService.ModelDownloadProgress>();
            service.ProgressChanged += p => events.Add(p);

            var ok = await service.DownloadModelAsync(
                "custom-x", "custom-x.onnx", "http://localhost/model.onnx", expectedSha,
                cancellationToken: TestContext.Current.CancellationToken);

            Check.That(ok).IsTrue();
            Check.That(File.ReadAllBytes(Path.Combine(tempDir, "custom-x.onnx")))
                .ContainsExactly(payload);
            Check.That(File.Exists(Path.Combine(tempDir, "custom-x.onnx.download"))).IsFalse();
            Check.That(events.Last().IsComplete).IsTrue();
            Check.That(events.Last().Error).IsNull();
            Check.That(events.First().Progress).IsEqualTo(0.0);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task DownloadModelAsync_HashMismatch_ReturnsFalseAndCleansUp()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var payload = System.Text.Encoding.UTF8.GetBytes("hello model payload");
            using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                }));
            using var service = new ModelDownloadService(tempDir, client);
            var events = new List<ModelDownloadService.ModelDownloadProgress>();
            service.ProgressChanged += p => events.Add(p);

            var ok = await service.DownloadModelAsync(
                "custom-x", "custom-x.onnx", "http://localhost/model.onnx",
                expectedSha256: "deadbeef",
                cancellationToken: TestContext.Current.CancellationToken);

            Check.That(ok).IsFalse();
            Check.That(File.Exists(Path.Combine(tempDir, "custom-x.onnx"))).IsFalse();
            Check.That(File.Exists(Path.Combine(tempDir, "custom-x.onnx.download"))).IsFalse();
            Check.That(events.Last().Error).IsEqualTo("Hash mismatch");
            Check.That(events.Last().IsComplete).IsTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}
