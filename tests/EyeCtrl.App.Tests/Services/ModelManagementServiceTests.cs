namespace EyeCtrl.App.Tests.Services;

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using EyeCtrl.App.Services;
using EyeCtrl.App.Tests.Settings.Pages;
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

            var result = await mgmt.DownloadCustomModelAsync(CustomModelUrl, TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsFalse();
            // The orphaned .onnx must be rolled back, mirroring the bundled path.
            Check.That(File.Exists(Path.Combine(tempDir, "repo.onnx"))).IsFalse();
            Check.That(settings.Keyboard.CustomModels).IsEmpty();
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

            var result = await mgmt.DownloadCustomModelAsync(CustomModelUrl, TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(result.ModelId).IsEqualTo("repo");

            Check.That(settings.Keyboard.CustomModels).HasSize(1);
            var custom = settings.Keyboard.CustomModels[0];
            Check.That(custom.SizeBytes).IsEqualTo(2048);
            Check.That(custom.Url).IsEqualTo(CustomModelUrl);
            Check.That(File.Exists(Path.Combine(tempDir, "repo.onnx"))).IsTrue();
            Check.That(File.Exists(Path.Combine(tempDir, "repo.tokenizer.json"))).IsTrue();
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
                "https://example.com/onnx/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsFalse();
            Check.That(File.Exists(Path.Combine(tempDir, "smollm2-135m.onnx"))).IsFalse();
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_Success_WithBothHashes_ReportsVerified()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var modelBytes = new byte[] { 0x05 };
            var tokBytes = new byte[] { 0x06 };
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/onnx/model.onnx",
                        Sha256 = ModelTestScope.Sha256Hex(modelBytes),
                        SizeBytes = modelBytes.Length,
                        TokenizerUrl = "https://example.com/onnx/tokenizer.json",
                        TokenizerSha256 = ModelTestScope.Sha256Hex(tokBytes),
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest,
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(tokBytes)
                    : Ok(modelBytes)));

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/onnx/model.onnx", manifest.Models["135M"].Sha256,
                "https://example.com/onnx/tokenizer.json", manifest.Models["135M"].TokenizerSha256,
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(registry.GetVerification("135M"))
                .IsEqualTo(ModelVerificationOutcome.Verified);
        }
        finally { Cleanup(tempDir); }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_Success_WithEmptyHashes_DoesNotReportVerified()
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
                        // Sha256 / TokenizerSha256 left empty: the download service skips the
                        // check (transport trust) — weaker than the stack writer's Verified.
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest,
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[] { 0x06 })
                    : Ok(new byte[] { 0x05 })));

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/onnx/model.onnx", "",
                "https://example.com/onnx/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(registry.GetVerification("135M"))
                .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        }
        finally { Cleanup(tempDir); }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_Success_ModelHashOnly_DoesNotReportVerified()
    {
        // The mixed arm of the M1+N4 gate: model hash checked, tokenizer hash empty (transport
        // trust on the tokenizer only). This is the one row that distinguishes the gate's &&
        // from an || — with an ||, a half-verified download would report Verified, and the
        // ledger term would then claim an integrity the tokenizer bytes never had.
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var modelBytes = new byte[] { 0x05 };
            var manifest = new ModelManifest
            {
                Models =
                {
                    ["135M"] = new ModelEntry
                    {
                        DisplayName = "SmolLM2 135M",
                        Url = "https://example.com/onnx/model.onnx",
                        Sha256 = ModelTestScope.Sha256Hex(modelBytes),
                        SizeBytes = modelBytes.Length,
                        TokenizerUrl = "https://example.com/onnx/tokenizer.json",
                        TokenizerSha256 = "",
                    },
                },
            };
            var (registry, mgmt) = BuildServices(tempDir, settings, manifest,
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[] { 0x06 })
                    : Ok(modelBytes)));

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/onnx/model.onnx", manifest.Models["135M"].Sha256,
                "https://example.com/onnx/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(registry.GetVerification("135M"))
                .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
        }
        finally { Cleanup(tempDir); }
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

            var first = mgmt.DownloadCustomModelAsync(CustomModelUrl, TestContext.Current.CancellationToken);
            await modelStarted.Task;

            Check.That(mgmt.IsDownloading).IsTrue();
            var second = await mgmt.DownloadCustomModelAsync(
                "https://huggingface.co/org/other/resolve/main/onnx/model.onnx",
                TestContext.Current.CancellationToken);
            Check.That(second.IsBusy).IsTrue();

            allowFinish.TrySetResult();
            var firstResult = await first;
            Check.That(firstResult.IsSuccess).IsTrue();
            Check.That(mgmt.IsDownloading).IsFalse();
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
        Check.That(name).IsEqualTo("repo");
    }

    [Fact]
    public void DeriveModelName_NestedPath_ReturnsLastSegmentBeforeResolve()
    {
        var name = ModelManagementService.DeriveModelName("https://huggingface.co/org/my-model-name/resolve/main/model.onnx");
        Check.That(name).IsEqualTo("my-model-name");
    }

    [Fact]
    public void DeriveModelName_NoResolveSegment_ReturnsLastSegment()
    {
        var name = ModelManagementService.DeriveModelName("https://example.com/models/foo");
        Check.That(name).IsEqualTo("foo");
    }

    [Fact]
    public void DeriveModelName_RootPath_ReturnsFallback()
    {
        var name = ModelManagementService.DeriveModelName("https://example.com/");
        Check.That(name).IsEqualTo("CustomModel");
    }

    [Fact]
    public void DeriveModelName_InvalidUrl_ReturnsFallback()
    {
        var name = ModelManagementService.DeriveModelName("not a url");
        Check.That(name).IsEqualTo("CustomModel");
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

            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("135M");
            Check.That(registry.ActiveModelId).IsEqualTo("135M");
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

            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("Off");
            Check.That(registry.ActiveModelId).IsNull();
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

            Check.That(registry.Registry.ContainsKey("135M")).IsTrue();
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

            Check.That(registry.Registry.ContainsKey("Custom")).IsTrue();

            mgmt.DeleteModel("Custom");

            Check.That(registry.Registry.ContainsKey("Custom")).IsFalse();
            Check.That(settings.Keyboard.CustomModels).IsEmpty();
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── Activation on download (ticket 8) ──
    //
    // The defect: a completed download wrote MLModelSize ZERO times. ActiveModelId is seeded
    // FROM MLModelSize (ModelRegistryService's constructor), so the stored default "360M" —
    // with no 360M file on disk — made the old inline "activate only if empty or Off"
    // conditional permanently false. Downloading a model could never activate it.

    [Fact]
    public async Task DownloadBundledModelAsync_StaleActiveModelId_ActivatesDownloadedModel()
    {
        var tempDir = NewTempDir();
        try
        {
            // The reporting user's shape: the setting (and therefore the registry seed) names
            // "360M", which was never downloaded. The model they actually downloaded is "1.5B".
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, ThreeBundledModels(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[64])
                    : Ok(new byte[2048])));

            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("360M");
            Check.That(registry.ActiveModelId).IsEqualTo("360M");
            Check.That(registry.IsModelDownloaded("360M")).IsFalse();

            var result = await mgmt.DownloadBundledModelAsync(
                "1.5B", "https://example.com/qwen/model.onnx", "",
                "https://example.com/qwen/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("1.5B");
            Check.That(registry.ActiveModelId).IsEqualTo("1.5B");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_RealDownloadedActiveModel_DoesNotSwitchAway()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, ThreeBundledModels(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[64])
                    : Ok(new byte[2048])));

            // A model the user genuinely has and deliberately selected.
            PutModelOnDisk(registry, "1.5B");
            mgmt.SetActiveModel("1.5B");
            Check.That(registry.IsModelDownloaded("1.5B")).IsTrue();

            var result = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/smol/model.onnx", "",
                "https://example.com/smol/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("1.5B");
            Check.That(registry.ActiveModelId).IsEqualTo("1.5B");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadBundledModelAsync_ActiveOff_ActivatesDownloadedModel()
    {
        var tempDir = NewTempDir();
        try
        {
            // "Off" is a deliberate choice, but activating here is not a silent override: the
            // download completes on the page the user is looking at, ApplyAndSave and
            // RebuildContent both fire, and the combo re-renders with the new model selected.
            // That is the difference from the startup repair this fix rejects — same outcome,
            // except there the user did nothing and would see nothing. Reversal costs one dwell
            // on a control they already used to reach "Off".
            //
            // Tripwire: this holds only while a download is a deliberate dwell + confirm on a
            // single caller. If a download ever starts itself (auto-update, background prefetch),
            // activating over "Off" becomes a silent override and this decision must be revisited.
            var settings = new EyeCtrlSettings();
            settings.Keyboard.MLModelSize = "Off";
            var (registry, mgmt) = BuildServices(tempDir, settings, ThreeBundledModels(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[64])
                    : Ok(new byte[2048])));

            Check.That(registry.ActiveModelId).IsEqualTo("Off");

            var result = await mgmt.DownloadBundledModelAsync(
                "1.5B", "https://example.com/qwen/model.onnx", "",
                "https://example.com/qwen/tokenizer.json", "",
                TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("1.5B");
            Check.That(registry.ActiveModelId).IsEqualTo("1.5B");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    [Fact]
    public async Task DownloadCustomModelAsync_StaleActiveModelId_ActivatesDownloadedModel()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, ThreeBundledModels(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[64])
                    : Ok(new byte[2048])));

            var result = await mgmt.DownloadCustomModelAsync(CustomModelUrl, TestContext.Current.CancellationToken);

            Check.That(result.IsSuccess).IsTrue();
            Check.That(result.ModelId).IsEqualTo("repo");
            Check.That(settings.Keyboard.MLModelSize).IsEqualTo("repo");
            Check.That(registry.ActiveModelId).IsEqualTo("repo");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    // ── The invariant (ticket 8) ──
    //
    // NOT the original defect: those two fields — the setting the prediction stack reads
    // (Keyboard.MLModelSize) and the registry's ActiveModelId — always AGREED, on a value no
    // model satisfied, and both download paths simply never wrote either of them. What this pin
    // protects is the regression the fix could introduce: a future entry point that updates one
    // field and not the other, the same class of divergence in the opposite direction. Every
    // entry point must leave the pair describing the same active model — "Off" deliberately has
    // two spellings (SetActiveModel normalizes to null on the registry; the constructor seed
    // stores it verbatim), so compare the EFFECTIVE value.

    [Fact]
    public async Task EveryEntryPoint_KeepsSettingAndRegistryInStep()
    {
        var tempDir = NewTempDir();
        try
        {
            var settings = new EyeCtrlSettings();
            var (registry, mgmt) = BuildServices(tempDir, settings, ThreeBundledModels(),
                req => Task.FromResult(req.RequestUri!.AbsolutePath.EndsWith("tokenizer.json")
                    ? Ok(new byte[64])
                    : Ok(new byte[2048])));

            CheckInStep(settings, registry, "the constructor seed");

            mgmt.SetActiveModel("1.5B");
            CheckInStep(settings, registry, "SetActiveModel");

            mgmt.SetActiveModel("Off");
            CheckInStep(settings, registry, "SetActiveModel(\"Off\"), which the registry normalizes to null");

            // Nothing real is active ("Off") and a download completes: it activates.
            var bundled = await mgmt.DownloadBundledModelAsync(
                "135M", "https://example.com/smol/model.onnx", "",
                "https://example.com/smol/tokenizer.json", "",
                TestContext.Current.CancellationToken);
            Check.That(bundled.IsSuccess).IsTrue();
            Check.That(registry.ActiveModelId).IsEqualTo("135M");
            CheckInStep(settings, registry, "a bundled download");

            mgmt.SetActiveModel("Off");
            var custom = await mgmt.DownloadCustomModelAsync(CustomModelUrl, TestContext.Current.CancellationToken);
            Check.That(custom.IsSuccess).IsTrue();
            Check.That(registry.ActiveModelId).IsEqualTo("repo");
            CheckInStep(settings, registry, "a custom download");
        }
        finally
        {
            Cleanup(tempDir);
        }
    }

    private static void CheckInStep(EyeCtrlSettings settings, ModelRegistryService registry, string entryPoint)
    {
        var effective = string.IsNullOrEmpty(registry.ActiveModelId) ? "Off" : registry.ActiveModelId;
        Check.WithCustomMessage(
                $"After {entryPoint}, Keyboard.MLModelSize ({settings.Keyboard.MLModelSize}) and "
                + $"registry.ActiveModelId ({registry.ActiveModelId ?? "<null>"}) must describe the "
                + "same active model: they are two views of one choice, written by two different "
                + "services, and this pair disagreeing is the defect.")
            .That(effective).IsEqualTo(settings.Keyboard.MLModelSize);
    }

    private static ModelManifest ThreeBundledModels() => new()
    {
        Models =
        {
            ["135M"] = new ModelEntry
            {
                DisplayName = "SmolLM2 135M",
                Url = "https://example.com/smol/model.onnx",
                TokenizerUrl = "https://example.com/smol/tokenizer.json",
            },
            ["360M"] = new ModelEntry
            {
                DisplayName = "SmolLM2 360M",
                Url = "https://example.com/smol360/model.onnx",
                TokenizerUrl = "https://example.com/smol360/tokenizer.json",
            },
            ["1.5B"] = new ModelEntry
            {
                DisplayName = "Qwen2.5 1.5B",
                Url = "https://example.com/qwen/model.onnx",
                TokenizerUrl = "https://example.com/qwen/tokenizer.json",
            },
        },
    };

    private static void PutModelOnDisk(ModelRegistryService registry, string modelId)
    {
        File.WriteAllText(registry.GetModelPath(modelId), "onnx");
        File.WriteAllText(registry.GetTokenizerPath(modelId), "{}");
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
