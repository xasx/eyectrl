namespace EyeCtrl.Architecture.Tests;

/// <summary>
/// C9 S6b ratchets for the signed publish (<c>tools/release/Publish-Signed.ps1</c>) and the thin
/// install wrapper (<c>tools/release/Install-EyeCtrl.ps1</c>).
///
/// <para>
/// These are text ratchets on purpose. The scripts are run by a human on the owner's machine, not
/// by CI: a publish signs with a TPM-backed certificate and writes the owner's update inbox, so no
/// test may execute either script (and CI must never sign). What CI <i>can</i> pin is the shape the
/// C9 plan and the package contract depend on: the pinned thumbprint and timestamp on every
/// signature, the UiAccess switch, the self-extraction refusal, the manifest expectations, the
/// catalog version, the three package.json fields, and Start-Process (never the call operator) in
/// the wrapper. A silent drift in any of those is the failure this file exists for.
/// </para>
/// </summary>
public class ReleaseScriptTests
{
    /// <summary>The S1a certificate's thumbprint (C9 plan §8, Q1). Signing with any other key is a defect.</summary>
    private const string PinnedThumbprint = "C1039769A2D9ED01F93CEBC78B61176EE6B12F91";

    private const string TimestampUrl = "http://timestamp.digicert.com";

    [Fact]
    public void ThePublishScript_SignsWithThePinnedCertificateAndTheDigiCertTimestamp()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the pinned certificate is the only signer this script may use")
            .That(script).Contains($"$CertificateThumbprint = '{PinnedThumbprint}'");
        Check.WithCustomMessage("the timestamp server is DigiCert's")
            .That(script).Contains($"$TimestampUrl = '{TimestampUrl}'");

        // The argument ORDER matters: /fd SHA256, then the pinned /sha1 and /s My, then the
        // RFC 3161 /tr with /td SHA256. A missing /td produces an SHA-1 timestamp on some
        // responders, and a missing /tr leaves an unsigned build that expires with the cert.
        var sign = FunctionBody(script, "Invoke-Sign");
        AssertContainsInOrder(
            sign,
            "'sign'",
            "'/fd'", "'SHA256'",
            "'/sha1'", "$CertificateThumbprint",
            "'/s'", "'My'",
            "'/tr'", "$TimestampUrl",
            "'/td'", "'SHA256'",
            "$FilePath");
    }

    [Fact]
    public void ThePublishScript_VerifiesEverySignatureBeforeItWritesThePackage()
    {
        var script = File.ReadAllText(PublishScriptPath);

        // signtool verify /pa is the Authenticode policy check; Get-AuthenticodeSignature adds the
        // thumbprint check that /pa alone cannot make.
        AssertContainsInOrder(FunctionBody(script, "Assert-Signed"), "verify", "/pa", "$FilePath");
        Check.WithCustomMessage("the signer must be checked against the pinned thumbprint, not just Valid")
            .That(script).Contains("$signature.SignerCertificate.Thumbprint");
        Check.WithCustomMessage("every exe found in the package must be signed")
            .That(script).Contains("Get-ChildItem -LiteralPath $packageDir -File -Filter '*.exe'");
    }

    [Fact]
    public void ThePublishScript_PublishesTheAppAsSingleFileSelfContainedWithUiAccess()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the App publish must pass the S2 build switch")
            .That(script).Contains("-p:EyeCtrlUiAccess=true");
        Check.WithCustomMessage("the package keeps single-file publishing")
            .That(script).Contains("-p:PublishSingleFile=true");
        Check.WithCustomMessage("the installed build is self-contained")
            .That(script).Contains("'--self-contained', 'true'");
        Check.WithCustomMessage("the App is published with the switch, the Supervisor without it")
            .That(script).Contains("Invoke-Publish $appProject $appOut $true");
        Check.WithCustomMessage("the Supervisor must never take the UiAccess switch")
            .That(script).Contains("Invoke-Publish $supervisorProject $supervisorOut $false");
    }

    [Fact]
    public void ThePublishScript_RefusesSelfExtractionBeforeItPublishes()
    {
        var script = File.ReadAllText(PublishScriptPath);

        var refusal = FunctionBody(script, "Assert-NoSelfExtraction");
        Check.WithCustomMessage("IncludeNativeLibrariesForSelfExtract must be refused")
            .That(refusal).Contains("IncludeNativeLibrariesForSelfExtract");
        Check.WithCustomMessage("IncludeAllContentForSelfExtract must be refused")
            .That(refusal).Contains("IncludeAllContentForSelfExtract");
        Check.WithCustomMessage("the refusal must explain the %TEMP% / High-IL reason")
            .That(refusal).Contains("%TEMP%");

        // Both the property gate and the result gate: a publish that lost its loose natives is the
        // silent self-extraction failure.
        Check.WithCustomMessage("the package must still hold loose native DLLs after publishing")
            .That(script).Contains("The package has no loose native DLLs");
        Check.WithCustomMessage("a bundled app assembly means single-file did not happen")
            .That(script).Contains("EyeCtrl.App.dll");
    }

    [Fact]
    public void ThePublishScript_ChecksTheEmbeddedManifestsOfAllThreeExes()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the App must embed uiAccess=\"true\"")
            .That(script).Contains("Assert-Manifest $appExe 'true'");
        Check.WithCustomMessage("the Supervisor must embed uiAccess=\"false\"")
            .That(script).Contains("Assert-Manifest $supervisorExe 'false'");
        Check.WithCustomMessage("Setup must embed uiAccess=\"false\"")
            .That(script).Contains("Assert-Manifest $setupExe 'false'");
        Check.WithCustomMessage("every exe must stay asInvoker, never requireAdministrator")
            .That(script).Contains("level=\"asInvoker\"");
        Check.WithCustomMessage("the manifest is read from the PE resource table, not from a running exe")
            .That(script).Contains("PEReader");
    }

    [Fact]
    public void ThePublishScript_WritesPackageJsonAndASha256Catalog()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("New-FileCatalog v2 is the SHA-256 catalog")
            .That(script).Contains("-CatalogVersion 2");
        Check.WithCustomMessage("the catalog is named EyeCtrl.cat")
            .That(script).Contains("'EyeCtrl.cat'");
        Check.WithCustomMessage("the catalog must be validated after it is written")
            .That(script).Contains("Test-FileCatalog");
        Check.WithCustomMessage("the catalog must be signed with the same certificate")
            .That(script).Contains("Invoke-Sign (Join-Path $packageDir 'EyeCtrl.cat')");

        var packageJson = FunctionBody(script, "Write-PackageJson");
        Check.That(packageJson).Contains("version =");
        Check.That(packageJson).Contains("sha     =");
        Check.That(packageJson).Contains("created =");
    }

    [Fact]
    public void ThePublishScript_WritesTheInboxOnlyForASignedPackage()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the inbox is %LOCALAPPDATA%\\EyeCtrl\\updates")
            .That(script).Contains("'EyeCtrl\\updates'");
        Check.WithCustomMessage("an unsigned package must never reach the real inbox")
            .That(script).Contains("Assert-NotInInbox $destination");
        Check.WithCustomMessage("the dry run must imply NoSign")
            .That(script).Contains("DryRun implies NoSign");

        // The dry-run contract: everything except signing and the inbox copy.
        Check.That(script).Contains("[switch]$DryRun");
        Check.That(script).Contains("[switch]$NoSign");
    }

    [Fact]
    public void TheInstallWrapper_StartsSetupThroughShellExecuteAndNeverTheCallOperator()
    {
        var script = File.ReadAllText(InstallScriptPath);

        Check.WithCustomMessage("Setup must be started with Start-Process")
            .That(script).Contains("Start-Process -FilePath $setupExe");
        Check.WithCustomMessage("the update verb and the package folder are the only arguments")
            .That(script).Contains("$SetupVerb = 'update'");
        Check.WithCustomMessage("the wrapper finds the newest package by package.json")
            .That(script).Contains("package.json");
        Check.WithCustomMessage("Setup's exe is the thing that gets started")
            .That(script).Contains("$SetupExeName = 'EyeCtrl.Setup.exe'");

        // The 740 trap: Start-Process is ShellExecute, the call operator is CreateProcess.
        // -NoNewWindow also flips Start-Process to CreateProcess, so it must never appear.
        Check.WithCustomMessage("the call operator would be CreateProcess, which fails with 740")
            .That(script).DoesNotContain("&");
        Check.WithCustomMessage("-NoNewWindow flips Start-Process to CreateProcess")
            .That(script).DoesNotContain("-NoNewWindow");
        Check.WithCustomMessage("no dynamic invocation can smuggle in a bare exe call")
            .That(script).DoesNotContain("Invoke-Expression");

        Check.WithCustomMessage("the wrapper has no install logic of its own: it never copies, deletes or writes")
            .That(script).DoesNotContain("Copy-Item");
        Check.WithCustomMessage("the wrapper must not write the registry")
            .That(script).DoesNotContain("New-ItemProperty");
    }

    [Fact]
    public void CiNeverPublishesOrSigns()
    {
        // The plan's invariant: CI stays unsigned and never sees a certificate. A workflow step
        // that ran the publish script would need the owner's TPM key, which must not exist in CI.
        var workflow = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "ci.yml"));

        Check.That(workflow).DoesNotContain("Publish-Signed");
        Check.That(workflow).DoesNotContain("Install-EyeCtrl");
        Check.That(workflow).DoesNotContain("signtool");
        Check.That(workflow).DoesNotContain(PinnedThumbprint);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the text between a top-level function's braces. The scripts keep their signtool
    /// argument lists inside one function each, so the assertions can be about those arguments and
    /// not about the whole file (which also mentions names like "verify" in prose).
    /// </summary>
    private static string FunctionBody(string script, string functionName)
    {
        var start = script.IndexOf($"function {functionName}", StringComparison.Ordinal);
        Check.WithCustomMessage($"function {functionName} must exist in the script")
            .That(start).IsStrictlyGreaterThan(-1);

        var open = script.IndexOf('{', start);
        Check.WithCustomMessage($"function {functionName} must have a body")
            .That(open).IsStrictlyGreaterThan(-1);

        var depth = 0;
        for (var i = open; i < script.Length; i++)
        {
            if (script[i] == '{')
            {
                depth++;
            }
            else if (script[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return script[(open + 1)..i];
                }
            }
        }

        throw new InvalidOperationException($"function {functionName} has unbalanced braces");
    }

    /// <summary>
    /// Asserts the tokens are all present and appear in the given order — the signtool contract is
    /// positional, so a mere Contains would accept a reordered or duplicated argument list.
    /// </summary>
    private static void AssertContainsInOrder(string text, params string[] tokens)
    {
        var position = 0;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, position, StringComparison.Ordinal);
            Check.WithCustomMessage(
                    $"'{token}' must appear after the previous argument (searching from offset {position})")
                .That(index).IsStrictlyGreaterThan(-1);
            position = index + token.Length;
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EyeCtrl.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate the repository root (EyeCtrl.slnx) above " + AppContext.BaseDirectory);
    }

    private static string PublishScriptPath =>
        Path.Combine(RepoRoot(), "tools", "release", "Publish-Signed.ps1");

    private static string InstallScriptPath =>
        Path.Combine(RepoRoot(), "tools", "release", "Install-EyeCtrl.ps1");
}
