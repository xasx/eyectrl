namespace EyeCtrl.Architecture.Tests;

/// <summary>
/// C9 S6b ratchets for the signed publish (<c>tools/release/Publish-Signed.ps1</c>) and the thin
/// install wrapper (<c>tools/release/Install-EyeCtrl.ps1</c>).
///
/// <para>
/// These are text ratchets on purpose. The scripts are run by a human on the owner's machine, not
/// by CI: a publish signs with a TPM-backed certificate and writes the owner's update inbox, so no
/// test may execute either script (and CI must never sign). What CI <i>can</i> pin is the shape the
/// C9 plan and the package contract depend on: the exact signtool argument list and its pinned
/// thumbprint and timestamp, the UiAccess switch on the App and compression on the Supervisor only,
/// the self-extraction refusal, the manifest expectations, the catalog version and package.json
/// fields, the atomic inbox delivery, and Start-Process (never the call operator, never its
/// tree-waiting flag) in the wrapper.
/// </para>
/// </summary>
public class ReleaseScriptTests
{
    /// <summary>The S1a certificate's thumbprint (C9 plan §8, Q1). Signing with any other key is a defect.</summary>
    private const string PinnedThumbprint = "C1039769A2D9ED01F93CEBC78B61176EE6B12F91";

    private const string TimestampUrl = "http://timestamp.digicert.com";

    [Fact]
    public void ThePublishScript_SignsWithExactlyThePinnedCertificateAndTheDigiCertTimestamp()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the pinned certificate is the only signer this script may use")
            .That(script).Contains($"$CertificateThumbprint = '{PinnedThumbprint}'");
        Check.WithCustomMessage("the timestamp server is DigiCert's")
            .That(script).Contains($"$TimestampUrl = '{TimestampUrl}'");

        // The argument list is pinned EXACTLY, element for element: an inserted /sm, a dropped
        // /td or a missing /tr changes the signature or its durability, and "contains in order"
        // would let an extra argument through (review-01 M6).
        var sign = FunctionBody(script, "Invoke-Sign");
        Check.WithCustomMessage("Invoke-Sign must build exactly one argument array")
            .That(Occurrences(sign, "@(")).IsEqualTo(1);

        // Joined and compared whole: same elements, same order, nothing inserted anywhere.
        Check.That(string.Join(" ", ArgumentList(sign)))
            .IsEqualTo("'sign' '/fd' 'SHA256' '/sha1' $CertificateThumbprint '/s' 'My' '/tr' $TimestampUrl '/td' 'SHA256' $FilePath");
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

        // The call line is pinned whole: flipping the switch off (review-01 M2) or compressing
        // the App both fail here.
        Check.That(SingleLineContaining(script, "Invoke-Publish -ProjectPath $appProject"))
            .IsEqualTo("Invoke-Publish -ProjectPath $appProject -OutDir $appOut -UiAccess");
    }

    [Fact]
    public void ThePublishScript_CompressesTheSupervisorButNeverTheApp()
    {
        // N1: the Supervisor is the one the plan allows to trade start-up time for 90 MB. The App
        // stays uncompressed so gaze control comes up as fast as today.
        var script = File.ReadAllText(PublishScriptPath);

        Check.That(SingleLineContaining(script, "Invoke-Publish -ProjectPath $supervisorProject"))
            .IsEqualTo("Invoke-Publish -ProjectPath $supervisorProject -OutDir $supervisorOut -CompressSingleFile");
        Check.WithCustomMessage("the compression is a Supervisor-only publish property")
            .That(script).Contains("-p:EnableCompressionInSingleFile=true");
        Check.WithCustomMessage("the App publish line must not compress")
            .That(SingleLineContaining(script, "Invoke-Publish -ProjectPath $appProject"))
            .DoesNotContain("CompressSingleFile");
        Check.WithCustomMessage("compression is not self-extraction: the Supervisor stays single-file")
            .That(script).Contains("-p:PublishSingleFile=true");
    }

    [Fact]
    public void ThePublishScript_RefusesSelfExtractionBeforeItPublishes()
    {
        var script = File.ReadAllText(PublishScriptPath);

        // The App is evaluated with the same build switch its publish uses, so a self-extract
        // property behind EyeCtrlUiAccess cannot hide from the gate.
        Check.WithCustomMessage("the App must be evaluated with the UiAccess switch on")
            .That(script).Contains("Assert-NoSelfExtraction $appProject $true");
        Check.WithCustomMessage("the Supervisor is evaluated without the switch")
            .That(script).Contains("Assert-NoSelfExtraction $supervisorProject $false");

        var refusal = FunctionBody(script, "Assert-NoSelfExtraction");
        Check.WithCustomMessage("IncludeNativeLibrariesForSelfExtract must be refused")
            .That(refusal).Contains("IncludeNativeLibrariesForSelfExtract");
        Check.WithCustomMessage("IncludeAllContentForSelfExtract must be refused")
            .That(refusal).Contains("IncludeAllContentForSelfExtract");
        Check.WithCustomMessage("the refusal must explain the %TEMP% / High-IL reason")
            .That(refusal).Contains("%TEMP%");
        Check.WithCustomMessage("the App's evaluation must pass the UiAccess switch")
            .That(refusal).Contains("-property:EyeCtrlUiAccess=true");

        // The result gate names the natives a working install needs, so "some dll" cannot pass.
        Check.That(script).Contains("$RequiredNativeDlls = @(");
        foreach (var native in new[] { "onnxruntime.dll", "tobii_stream_engine.dll", "wpfgfx_cor3.dll", "PresentationNative_cor3.dll" })
        {
            Check.WithCustomMessage($"the loose-native gate must require {native}")
                .That(script).Contains($"'{native}'");
        }
        Check.That(script).Contains("The package is missing the loose native");
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
    public void ThePublishScript_WritesPackageJsonAsAMemberOfASha256Catalog()
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

        // The contract: package.json is written BEFORE the catalog, so it is a catalog member and
        // any later edit makes the package fail verification.
        var jsonIndex = script.IndexOf("Write-PackageJson $packageDir $packageVersion $sha", StringComparison.Ordinal);
        var catalogIndex = script.IndexOf("New-PackageCatalog $packageDir", StringComparison.Ordinal);
        Check.WithCustomMessage("package.json must be written before the catalog")
            .That(jsonIndex).IsStrictlyGreaterThan(-1);
        Check.WithCustomMessage("the catalog must be created after package.json")
            .That(catalogIndex).IsStrictlyGreaterThan(jsonIndex);
    }

    [Fact]
    public void ThePublishScript_TakesThePackageVersionFromTheAppItself()
    {
        // F2: -Version only bumps the MSBuild version. The folder, package.json and the App must
        // all say <version>+<sha>, which is what Get-AppVersion reads out of the built apphost.
        var script = File.ReadAllText(PublishScriptPath);

        Check.That(script).Contains("$packageVersion = Get-AppVersion $packagedApp");
        Check.WithCustomMessage("no branch may let -Version override the App's own informational version")
            .That(script).DoesNotContain("$packageVersion = if ($Version)");
    }

    [Fact]
    public void ThePublishScript_WritesTheInboxOnlyForASignedInstallablePackage()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the inbox is %LOCALAPPDATA%\\EyeCtrl\\updates")
            .That(script).Contains("'EyeCtrl\\updates'");
        Check.WithCustomMessage("an unsigned package must never reach the real inbox")
            .That(script).Contains("Assert-NotInInbox $destination 'an unsigned package");
        Check.WithCustomMessage("a package without Setup must never reach the real inbox")
            .That(script).Contains("Assert-NotInInbox $destination 'a package without EyeCtrl.Setup");
        Check.WithCustomMessage("the dry run must imply NoSign")
            .That(script).Contains("DryRun implies NoSign");

        Check.That(script).Contains("[switch]$DryRun");
        Check.That(script).Contains("[switch]$NoSign");
    }

    [Fact]
    public void ThePublishScript_DeliversTheInboxAtomically()
    {
        // F4: a half-copied package must never be visible under updates\<version>.
        var script = File.ReadAllText(PublishScriptPath);

        Check.WithCustomMessage("the copy must go through a dot-prefixed .partial sibling")
            .That(script).Contains("'.' + (Split-Path -Leaf $destination) + '.partial'");
        Check.WithCustomMessage("the partial package must validate before it is renamed")
            .That(script).Contains("Assert-CatalogValid $partial");
        Check.WithCustomMessage("the rename is the publish")
            .That(script).Contains("Rename-Item -LiteralPath $partial -NewName");
        Check.WithCustomMessage("a wildcard -Path misreads [ and ] in the scratch path")
            .That(script).DoesNotContain("Join-Path $packageDir '*'");
        Check.That(script).Contains("Copy-PackageTree $packageDir $partial");
    }

    [Fact]
    public void ThePublishScript_KeepsBuildArtifactsOutOfThePackage()
    {
        var script = File.ReadAllText(PublishScriptPath);

        Check.That(script).Contains("$PackageNoiseExtensions = @('.pdb', '.lib')");
        Check.That(script).Contains("$PackageNoiseNames = @('README.md')");
        Check.That(script).Contains("Remove-PackageNoise $packageDir");
        Check.WithCustomMessage("symbols are not generated for the publish")
            .That(script).Contains("'-p:DebugType=None'");
        Check.WithCustomMessage("XML docs are not generated for the publish")
            .That(script).Contains("'-p:GenerateDocumentationFile=false'");
    }

    [Fact]
    public void ThePublishScript_RefusesADirtyTreeOnASigningRun()
    {
        // The package's sha claims HEAD, and the package is signed: a dirty tree makes that false.
        var script = File.ReadAllText(PublishScriptPath);

        Check.That(script).Contains("The working tree is dirty");
        Check.WithCustomMessage("a dirty tree is an error on a signing run, not a warning")
            .That(script).Contains("throw (\"The working tree is dirty:");
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

        Check.WithCustomMessage("the wrapper has no install logic of its own: it never copies")
            .That(script).DoesNotContain("Copy-Item");
        Check.WithCustomMessage("the wrapper must not write the registry")
            .That(script).DoesNotContain("New-ItemProperty");
    }

    [Fact]
    public void TheInstallWrapper_WaitsForSetupOnly_NotForItsProcessTree()
    {
        // F1: Start-Process's own wait flag waits on the job object, which holds the App that
        // Setup launches. -PassThru plus WaitForExit waits for Setup itself.
        var script = File.ReadAllText(InstallScriptPath);

        Check.WithCustomMessage("the wrapper must never pass the tree-waiting flag")
            .That(script).DoesNotContain("-Wait");
        Check.That(script).Contains("Start-Process -FilePath $setupExe -ArgumentList $setupArguments -WorkingDirectory $PackageDir -PassThru");
        Check.That(script).Contains("$process.WaitForExit()");
    }

    [Fact]
    public void TheInstallWrapper_TrimsATrailingSeparatorAndSkipsPartialPackages()
    {
        // F3: GetFullPath('C:\x\') keeps the '\', and update "C:\x\" arrives as C:\x".
        var script = File.ReadAllText(InstallScriptPath);

        Check.That(script).Contains("TrimEndingDirectorySeparator");
        Check.WithCustomMessage("the .<version>.partial folder must not be treated as a package")
            .That(script).Contains("-not $_.Name.StartsWith('.')");

        // Contract order: created, then version, then name.
        AssertContainsInOrder(
            script,
            "Expression = 'Created'",
            "Expression = 'VersionKey'",
            "Expression = 'Name'");
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
    /// Returns the elements of the first <c>@( … )</c> array literal in a function body, in source
    /// order. Splitting on commas is enough because every element is a single token.
    /// </summary>
    private static string[] ArgumentList(string functionBody)
    {
        var start = functionBody.IndexOf("@(", StringComparison.Ordinal);
        Check.WithCustomMessage("the function must build an argument array")
            .That(start).IsStrictlyGreaterThan(-1);
        var end = functionBody.IndexOf(')', start);
        Check.WithCustomMessage("the argument array must be closed")
            .That(end).IsStrictlyGreaterThan(start);

        return functionBody[(start + 2)..end]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Returns the trimmed single line that contains <paramref name="marker"/>.</summary>
    private static string SingleLineContaining(string text, string marker)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                return line.Trim();
            }
        }
        throw new InvalidOperationException($"no line contains '{marker}'");
    }

    /// <summary>
    /// Asserts the tokens are all present and appear in the given order.
    /// </summary>
    private static void AssertContainsInOrder(string text, params string[] tokens)
    {
        var position = 0;
        foreach (var token in tokens)
        {
            var index = text.IndexOf(token, position, StringComparison.Ordinal);
            Check.WithCustomMessage(
                    $"'{token}' must appear after the previous token (searching from offset {position})")
                .That(index).IsStrictlyGreaterThan(-1);
            position = index + token.Length;
        }
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
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
