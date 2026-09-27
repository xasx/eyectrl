namespace EyeCtrl.App.Tests.Services;

using System.IO;
using EyeCtrl.App.Services;
using EyeCtrl.Core.Prediction;
using EyeCtrl.Core.Settings;
using EyeCtrl.App.Tests;
using EyeCtrl.App.Tests.Settings.Pages;

public class PredictionStackFactoryVerificationTests : IDisposable
{
    private readonly ModelTestScope _scope = ModelTestScope.Create();
    public void Dispose() => _scope.Dispose();

    private static EyeCtrlSettings SettingsFor(string modelSize) => new()
    {
        Keyboard = { PredictionEnabled = true, MLSuggestionsEnabled = true, MLModelSize = modelSize },
    };

    // The C2 classifier, table-tested (plan review M1): these six rows are the whole decision,
    // so a mutation that misreports any cause fails here.
    [Theory]
    [InlineData(false, true, true, true, true)]  // model absent        -> nothing reported
    [InlineData(true, false, true, true, true)]  // no manifest hash    -> nothing reported
    [InlineData(true, true, false, true, true)]  // hash mismatch       -> Failed (even though
                                                 //                        the PREDICTOR would be fine)
    [InlineData(true, true, true, false, true)]  // predictor null      -> Failed
    [InlineData(true, true, true, true, false)]  // tokenizer missing   -> Failed
    [InlineData(true, true, true, true, true)]   // everything live     -> Verified
    public void ClassifyLmOutcome_CoversTheWholeDecisionTable(
        bool modelOnDisk, bool hashGated, bool hashOk,
        bool predictorAvailable, bool tokenizerLoaded)
    {
        var (reports, outcome) = PredictionStackFactory.ClassifyLmOutcome(
            modelOnDisk, hashGated, hashOk, predictorAvailable, tokenizerLoaded);

        if (!modelOnDisk || !hashGated)
        {
            Check.That(reports).IsFalse();
        }
        else if (!hashOk || !predictorAvailable || !tokenizerLoaded)
        {
            Check.That(reports).IsTrue();
            Check.That(outcome).IsEqualTo(ModelVerificationOutcome.Failed);
        }
        else
        {
            Check.That(reports).IsTrue();
            Check.That(outcome).IsEqualTo(ModelVerificationOutcome.Verified);
        }
    }

    private sealed class AvailableLmPredictor : ILanguageModelPredictor
    {
        public bool IsAvailable => true;
        public void Predict(string context, string? prefix = null) { }
        public IReadOnlyList<PredictionResult>? TryGetResults() => null;
        public void Cancel() { }
        public void Dispose() { }
    }

    // M1's integration arm: a live predictor + a REAL loaded tokenizer MUST classify Verified
    // through the sync build, so a mutation that hard-codes Failed cannot ship silently (it
    // would show the repair row on every healthy install and loop re-downloads). The tokenizer
    // is real (the fixture) and loaded by the production loader — only the ONNX predictor is
    // overridden.
    [Fact]
    public void SyncBuild_LivePredictorAndTokenizer_ReportsVerified()
    {
        var (registry, _) = _scope.WithHashedDownloadedBundled();
        var factory = new PredictionStackFactory
        {
            PredictorFactoryOverride = (_, _, _, _) => new AvailableLmPredictor(),
        };

        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Verified);
        Check.That(factory.Current!.LanguageModel).IsNotNull();
        Check.That(factory.Current!.LanguageModel!.IsAvailable).IsTrue();
    }

    [Fact]
    public void SyncBuild_PresentHashGatedModel_UnavailableLm_ReportsFailed()
    {
        var (registry, _) = _scope.WithHashedDownloadedBundled();

        var factory = new PredictionStackFactory(); // sync mode: Build completes inside Rebuild
        var orchestrator = factory.Rebuild(SettingsFor("135M"),
            AppDomain.CurrentDomain.BaseDirectory, _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
    }

    // Plan review C1: the original code CONSTRUCTS the predictor for every model that passes
    // the mismatch gate — including custom models, whose manifest hash is always empty. The
    // revision-1 plan dropped that construction in the no-report arm; this row pins it.
    [Fact]
    public void SyncBuild_EmptyManifestHash_StillConstructsTheLm()
    {
        var (registry, _) = _scope.WithDownloadedBundled(); // Sha256 = "" (custom-style)
        var factory = new PredictionStackFactory();

        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified); // nothing reported
        Check.That(factory.Current!.LanguageModel).IsNotNull();  // but the LM is built
    }

    [Fact]
    public void SyncBuild_HashMismatch_PredictorNotConstructed_ReportsFailed()
    {
        // Manifest hashes that do NOT match the on-disk bytes: mismatch arm — no predictor,
        // Failed reported (the SmallLMPredictor is deliberately not constructed today either).
        var (registry, _) = _scope.WithHashedDownloadedBundled();
        var settings = SettingsFor("135M");
        var tampered = new ModelManifest
        {
            Models =
            {
                ["135M"] = new ModelEntry
                {
                    DisplayName = "Test 135M",
                    Url = "https://example.invalid/model.onnx",
                    Sha256 = new string('a', 64), // wrong hash
                    SizeBytes = 1,
                    TokenizerUrl = "https://example.invalid/tokenizer.json",
                    TokenizerSha256 = new string('b', 64),
                },
            },
        };
        // Rebuild the registry over the same dir with the tampered manifest (same ctor shape
        // as ModelTestScope.BuildRegistry): the FILES stay, the EXPECTED hashes differ.
        registry = new ModelRegistryService(_scope.Dir, tampered, new EyeCtrlSettings());

        var factory = new PredictionStackFactory();
        factory.Rebuild(settings, AppDomain.CurrentDomain.BaseDirectory, _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.Failed);
        Check.That(factory.Current!.LanguageModel).IsNull();
    }

    [Fact]
    public void SyncBuild_ModelFileAbsent_ReportsNothing()
    {
        var (registry, _) = _scope.WithHashedDownloadedBundled();
        var onnx = Path.Combine(_scope.Dir, "smollm2-135m.onnx");
        var tok = Path.Combine(_scope.Dir, "smollm2-135m.tokenizer.json");
        if (File.Exists(onnx)) File.Delete(onnx);
        if (File.Exists(tok)) File.Delete(tok);

        var factory = new PredictionStackFactory();
        factory.Rebuild(SettingsFor("135M"), AppDomain.CurrentDomain.BaseDirectory,
            _scope.Dir, registry);

        Check.That(registry.GetVerification("135M"))
            .IsEqualTo(ModelVerificationOutcome.NotYetVerified);
    }

    [Fact]
    public void OpenForHash_AllowsTheInstallReplace_AgainstAnOpenReader()
    {
        // Issue #166 round-2 M-1, behavioural: the hash-verification stream must hold
        // FileShare.Delete or every repair re-download dies mid-install with IOException
        // (mutation X4 — reverting VerifyFileSha256 to File.OpenRead — survived 1197 green).
        // The reader is opened through PRODUCTION's own OpenForHash, so this probes the real
        // share mode, not a test-side copy of it.
        var dir = Path.Combine(Path.GetTempPath(), "pfv_test_" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            var dst = Path.Combine(dir, "model.onnx");
            var incoming = dst + ".incoming";
            File.WriteAllText(dst, new string('a', 8192));
            File.WriteAllText(incoming, new string('b', 8192));

            using var hashStream = PredictionStackFactory.OpenForHash(dst);

            File.Replace(incoming, dst, destinationBackupFileName: null);

            Check.That(File.ReadAllText(dst)[0]).IsEqualTo('b');
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifyFileSha256_OpensThroughOpenForHash_NotFileDotOpenRead()
    {
        // Belt-and-braces source pin over COMMENT-STRIPPED, whitespace-collapsed code (the M3
        // discipline): X4's inline revert to File.OpenRead cannot pass this, and the
        // behavioural test above cannot catch it because the revert lives inside
        // VerifyFileSha256's own body.
        var source = System.Text.RegularExpressions.Regex.Replace(
            SourceScanner.StripComments(File.ReadAllText(System.IO.Path.Combine(
                SourceScanner.RepoRoot().FullName,
                "src", "EyeCtrl.App", "Services", "PredictionStackFactory.cs"))),
            @"\s+", string.Empty);

        Check.WithCustomMessage(
                "VerifyFileSha256 no longer opens through OpenForHash — a plain File.OpenRead " +
                "(FileShare.Read, no Delete) makes the repair install's File.Replace die " +
                "mid-install while every suite stays green (round-2 mutation X4).")
            .That(source.Contains("usingvarstream=OpenForHash(path);")).IsTrue();
    }
}
