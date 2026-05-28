namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using Xunit;

public class ModelManagementServiceTests
{
    private const string CustomModelUrl = "https://huggingface.co/org/repo/resolve/main/onnx/model.onnx";

    [Fact]
    public async Task DownloadCustomModelAsync_TokenizerFails_RemovesModelFileAndDoesNotRegister()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, new ModelManifest(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? NotFound()
                    : Ok(new byte[2048])));

            var result = await mgmt.DownloadCustomModelAsync(CustomModelUrl);

            Assert.False(result.IsSuccess);
            // The orphaned .onnx must be rolled back, mirroring the bundled path.
            Assert.False(File.Exists(Path.Combine(tempDir, "repo.onnx")));
            Assert.Empty(settings.Keyboard.CustomModels);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadCustomModelAsync_Success_RecordsActualFileSizeAndRegisters()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, new ModelManifest(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[64])
                    : Ok(new byte[2048])));

            var result = await mgmt.DownloadCustomModelAsync(CustomModelUrl);

            Assert.True(result.IsSuccess);
            Assert.Equal("repo", result.ModelId);

            var custom = Assert.Single(settings.Keyboard.CustomModels);
            Assert.Equal(2048, custom.SizeBytes);
            Assert.Equal(CustomModelUrl, custom.Url);
            Assert.True(File.Exists(Path.Combine(tempDir, "repo.onnx")));
            Assert.True(File.Exists(Path.Combine(tempDir, "repo.tokenizer.json")));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_TokenizerFails_RemovesModelFile()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/onnx/model.onnx",
                        TokenizerUrl = "https://example.com/onnx/tokenizer.json",
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest,
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? NotFound()
                    : Ok(new byte[2048])));

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/onnx/model.onnx", "",
                "https://example.com/onnx/tokenizer.json", "");

            Assert.False(result.IsSuccess);
            Assert.False(File.Exists(Path.Combine(tempDir, "smollm2-135m.onnx")));
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadCustomModelAsync_WhileWorkflowActive_SecondCallReturnsBusy()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var modelStarted = new TaskCompletionSource();
            var allowFinish = new TaskCompletionSource();

            var (_, mgmt) = BuildServices(tempDir, settings, new ModelManifest(), async req =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json"))
                    return Ok(new byte[64]);

                // Model request: signal the workflow is in-flight (gate held), then block until released.
                modelStarted.TrySetResult();
                await allowFinish.Task;
                return Ok(new byte[2048]);
            });

            var first = mgmt.DownloadCustomModelAsync(CustomModelUrl);
            await modelStarted.Task;

            Assert.True(mgmt.IsDownloading);
            var second = await mgmt.DownloadCustomModelAsync(
                "https://huggingface.co/org/other/resolve/main/onnx/model.onnx");
            Assert.True(second.IsBusy);

            allowFinish.TrySetResult();
            var firstResult = await first;
            Assert.True(firstResult.IsSuccess);
            Assert.False(mgmt.IsDownloading);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public void DeriveModelName_HuggingFaceUrl_ReturnsRepoName()
    {
        var name = ModelManagementService.DeriveModelName(CustomModelUrl);
        Assert.Equal("repo", name);
    }

    [Fact]
    public void DeriveModelName_NestedPath_ReturnsLastSegmentBeforeResolve()
    {
        var name = ModelManagementService.DeriveModelName("https://huggingface.co/org/my-model-name/resolve/main/model.onnx");
        Assert.Equal("my-model-name", name);
    }

    [Fact]
    public void DeriveModelName_NoResolveSegment_ReturnsLastSegment()
    {
        var name = ModelManagementService.DeriveModelName("https://example.com/models/foo");
        Assert.Equal("foo", name);
    }

    [Fact]
    public void DeriveModelName_RootPath_ReturnsFallback()
    {
        var name = ModelManagementService.DeriveModelName("https://example.com/");
        Assert.Equal("CustomModel", name);
    }

    [Fact]
    public void DeriveModelName_InvalidUrl_ReturnsFallback()
    {
        var name = ModelManagementService.DeriveModelName("not a url");
        Assert.Equal("CustomModel", name);
    }

    [Fact]
    public void SetActiveModel_UpdatesSettingsAndRegistry()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var manifest = new ModelManifest();
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest, _ => Task.FromResult(NotFound()));

            mgmt.SetActiveModel("135M");

            Assert.Equal("135M", settings.Keyboard.MLModelSize);
            Assert.Equal("135M", registry.ActiveModelId);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public void SetActiveModel_Off_SetsNullAndWritesOff()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, new ModelManifest(), _ => Task.FromResult(NotFound()));

            mgmt.SetActiveModel("Off");

            Assert.Equal("Off", settings.Keyboard.MLModelSize);
            Assert.Null(registry.ActiveModelId);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public void DeleteModel_BundledModel_DeletesFilesButKeepsRegistry()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/model.onnx",
                        TokenizerUrl = "https://example.com/tokenizer.json",
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest, _ => Task.FromResult(NotFound()));

            mgmt.DeleteModel("135M");

            Assert.True(registry.Registry.ContainsKey("135M"), "Bundled model should remain in registry");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public void DeleteModel_CustomModel_RemovesFromRegistryAndSettings()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, new ModelManifest(), _ => Task.FromResult(NotFound()));

            registry.AddCustomModel("Custom", "Custom", "https://example.com/model.onnx",
                "", 0, "https://example.com/tokenizer.json", "", settings.Keyboard);

            Assert.True(registry.Registry.ContainsKey("Custom"));

            mgmt.DeleteModel("Custom");

            Assert.False(registry.Registry.ContainsKey("Custom"));
            Assert.Empty(settings.Keyboard.CustomModels);
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    private static (ModelRegistryService, ModelManagementService) BuildServices(
        string tempDir, EyeCtrlSettings settings, ModelManifest manifest,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StubHttpMessageHandler(responder));
        var download = new ModelDownloadService(tempDir, httpClient);
        var registry = new ModelRegistryService(tempDir, manifest, settings);
        var mgmt = new ModelManagementService(registry, download, settings);
        return (registry, mgmt);
    }

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mgmt_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) =>
            _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            _responder(request);
    }
}
