namespace EyeCtrl.Core.Tests.Prediction;

using EyeCtrl.Core.Prediction;

public class ModelDownloadServiceTests
{
    [Fact]
    public void GetAvailableModels_NoModelsDirectory_ReturnsEmpty()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            var service = new ModelDownloadService(tempDir);
            var available = service.GetAvailableModels();
            Check.That(available).IsEmpty();
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAvailableModels_WithValidFiles_ReturnsModelSizes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.tokenizer.json"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-360m.onnx"), "dummy");
            File.WriteAllText(Path.Combine(tempDir, "smollm2-360m.tokenizer.json"), "dummy");

            var service = new ModelDownloadService(tempDir);
            var available = service.GetAvailableModels();

            Check.That(available).Contains("135M");
            Check.That(available).Contains("360M");
            Check.That(available.Contains("1.5B")).IsFalse();
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
    public void GetAvailableModels_WithOnnxButNoTokenizer_ExcludesModel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            File.WriteAllText(Path.Combine(tempDir, "smollm2-135m.onnx"), "dummy");
            var service = new ModelDownloadService(tempDir);

            var available = service.GetAvailableModels();

            Check.That(available.Contains("135M")).IsFalse();
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

    private sealed class EndlessStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(20);
            Array.Clear(buffer, offset, count);
            return count;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(20, ct);
            buffer.Span.Clear();
            return buffer.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CancelDownload_CancelsTokenizerDownload()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"model_test_{Guid.NewGuid()}");
        try
        {
            using var client = new HttpClient(new StubHttpMessageHandler((_, _) =>
                new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StreamContent(new EndlessStream())
                }));
            using var service = new ModelDownloadService(tempDir, client);

            // The test token is an ADDITIONAL cancellation source only — the assertion below still
            // depends on service.CancelDownload() being what cancels the download.
            var task = service.DownloadTokenizerAsync("360M", "http://localhost/tokenizer.json", "",
                cancellationToken: TestContext.Current.CancellationToken);

            var start = Environment.TickCount64;
            while (!service.IsDownloading)
            {
                Check.That(Environment.TickCount64 - start).IsStrictlyLessThan(2000);
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            service.CancelDownload();

            var completed = await Task.WhenAny(
                task, Task.Delay(5000, TestContext.Current.CancellationToken));
            Check.That(completed).IsSameReferenceAs(task);

            OperationCanceledException? oce = null;
            try { await task; }
            catch (OperationCanceledException ex) { oce = ex; }
            Check.That(oce).IsNotNull();
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
