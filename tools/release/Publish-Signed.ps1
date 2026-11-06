#Requires -Version 7.0
<#
.SYNOPSIS
    Builds, signs and packages the installed (UIAccess) EyeCtrl build into the update inbox.

.DESCRIPTION
    Publishes EyeCtrl.App (single-file, self-contained, uiAccess="true") and EyeCtrl.Supervisor
    (single-file, self-contained, uiAccess="false", compressed) into one package folder, plus
    EyeCtrl.Setup when src/EyeCtrl.Setup exists. Each publish output keeps its loose native DLLs
    beside the single-file exe: self-extraction would load native code from a user-writable %TEMP%
    into a High-IL process, so it is refused. Loose symbols, XML docs, import libraries and README
    files are build artifacts and are pruned from the package; symbols stay embedded in the
    bundles so the owner's stack traces keep file:line.

    With signing on, every exe is signed with the pinned self-signed certificate and the DigiCert
    timestamp, each signature is verified, each embedded manifest is read back and checked, and
    the file catalog (New-FileCatalog v2, SHA-256) is created and signed the same way. The package
    is then copied to %LOCALAPPDATA%\EyeCtrl\updates\<version>\ through a .<version>.partial folder
    and a rename, so a half-written package is never visible.

    -DryRun -NoSign does everything except signing and the inbox copy. It is the only mode an
    agent or a reviewer may run: it never touches the real inbox, Program Files or the registry,
    and it never runs a built exe.

    Never sign in a dev, test or CI session. Dev, test and CI builds stay unsigned with
    uiAccess="false"; only the owner's machine signs and installs, and only on the owner's go.

    The package contract (shared with src/EyeCtrl.Setup):
      - every exe is signed and timestamped; EyeCtrl.cat is signed with the same certificate;
      - EyeCtrl.cat is a New-FileCatalog v2 (SHA-256) catalog over every other file, recursively;
      - a catalog member's tag is the uppercase hex of its SIP SHA-256 hash (for a PE that is the
        Authenticode hash), and its relative path is the FilePath attribute;
      - package.json is a catalog member (only EyeCtrl.cat itself is outside it), so editing it
        makes the package fail verification;
      - package.json is { version, sha, created }: version is the App's informational version,
        sha the git commit, created an ISO-8601 timestamp.

.PARAMETER DryRun
    Build and package into -ScratchDir (or -OutputDir) only. Implies -NoSign and refuses any
    destination under %LOCALAPPDATA%\EyeCtrl\updates.

.PARAMETER NoSign
    Skip signtool entirely (no signing, no signature verification). A NoSign run may not write
    the real inbox: the catalog would be unsigned and Setup would refuse the package anyway.

.PARAMETER SkipSetup
    Package App + Supervisor without EyeCtrl.Setup. A package without Setup cannot install
    itself, so it never reaches the real inbox, and a signing run refuses a missing Setup unless
    this switch is given.

.PARAMETER Force
    Replace an existing destination folder of the same version.

.PARAMETER Version
    MSBuild version for the three publishes (-p:Version). The package folder and package.json
    always use the App's own informational version, which carries +<sha>; this only bumps the
    numeric part.

.PARAMETER OutputDir
    Final package destination. Default: the inbox in a signing run, and the scratch package
    folder in a dry run.

.PARAMETER ScratchDir
    Where the per-project publish outputs and the assembled package are built. Default: a
    timestamped folder under %TEMP%.

.PARAMETER SignToolPath
    signtool.exe to use. Default: signtool on PATH, else the newest Windows 10 SDK x64 signtool.
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$NoSign,
    [switch]$SkipSetup,
    [switch]$Force,
    [string]$Version,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir,
    [string]$ScratchDir,
    [string]$SignToolPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The S1a certificate: TPM-backed, Code Signing EKU only, valid to 03.11.2036, trusted in
# LocalMachine\Root on the owner's machine. The private key never leaves the TPM and is never
# exported; this script only ever asks signtool to use it.
$CertificateThumbprint = 'C1039769A2D9ED01F93CEBC78B61176EE6B12F91'
$TimestampUrl = 'http://timestamp.digicert.com'

$RequiredPackageExes = @('EyeCtrl.App.exe', 'EyeCtrl.Supervisor.exe')
$SetupExeName = 'EyeCtrl.Setup.exe'

# The loose natives a working installed build needs. Their absence is the silent self-extraction
# signature (and, for the Tobii engine, a package without eye tracking). WebView2Loader.dll is
# the gaze browser's native loader (T5/external review M12): Microsoft.Web.WebView2.Core.dll
# P/Invokes it, so a package without it ships a browser that cannot start.
$RequiredNativeDlls = @(
    'onnxruntime.dll',
    'tobii_stream_engine.dll',
    'wpfgfx_cor3.dll',
    'PresentationNative_cor3.dll',
    'WebView2Loader.dll'
)

# Build artifacts that must not ship to Program Files. '.xml' covers the WebView2 managed
# assemblies' IntelliSense doc files (review M12 — package noise, not user content).
$PackageNoiseExtensions = @('.pdb', '.lib', '.xml')
$PackageNoiseNames = @('README.md')

function Write-Step([string]$Message)
{
    Write-Host ''
    Write-Host "== $Message" -ForegroundColor Cyan
}

function Write-Note([string]$Message)
{
    Write-Host "   $Message" -ForegroundColor DarkGray
}

function Write-WarningLine([string]$Message)
{
    Write-Host "WARNING: $Message" -ForegroundColor Yellow
}

function Get-RepoRoot
{
    $dir = [System.IO.DirectoryInfo]::new($PSScriptRoot)
    while ($null -ne $dir)
    {
        if (Test-Path -LiteralPath (Join-Path $dir.FullName 'EyeCtrl.slnx'))
        {
            return $dir.FullName
        }
        $dir = $dir.Parent
    }
    throw "Could not locate the repository root (EyeCtrl.slnx) above $PSScriptRoot"
}

function Invoke-Native([string]$FilePath, [string[]]$Arguments)
{
    Write-Note "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0)
    {
        throw "$FilePath exited with code $exitCode ($($Arguments -join ' '))"
    }
}

function Resolve-SignTool([string]$Explicit)
{
    if ($Explicit)
    {
        if (-not (Test-Path -LiteralPath $Explicit))
        {
            throw "signtool.exe not found at -SignToolPath '$Explicit'"
        }
        return (Resolve-Path -LiteralPath $Explicit).Path
    }

    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($null -ne $onPath)
    {
        return $onPath.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot)
    {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object -Property Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($candidate)
        {
            return $candidate
        }
    }

    throw 'signtool.exe not found: pass -SignToolPath, or install the Windows 10/11 SDK.'
}

# ── Embedded manifest, read straight from the PE resource table ──────────────────────────────
# No process is started and no image is loaded: PEReader only parses the file bytes.

function Get-EmbeddedManifestText([string]$ExePath)
{
    $stream = [System.IO.File]::Open($ExePath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try
    {
        $image = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try
        {
            $headers = $image.PEHeaders
            $directory = $headers.PEHeader.ResourceTableDirectory
            if ($directory.RelativeVirtualAddress -le 0)
            {
                throw "${ExePath}: no PE resource table, so no embedded manifest"
            }

            $sectionBase = -1
            foreach ($section in $headers.SectionHeaders)
            {
                $end = $section.VirtualAddress + [Math]::Max($section.VirtualSize, $section.SizeOfRawData)
                if ($directory.RelativeVirtualAddress -ge $section.VirtualAddress -and
                    $directory.RelativeVirtualAddress -lt $end)
                {
                    $sectionBase = $section.VirtualAddress
                    break
                }
            }
            if ($sectionBase -lt 0)
            {
                throw "${ExePath}: the resource directory is outside every section"
            }

            $block = $image.GetSectionData($directory.RelativeVirtualAddress)
            $reader = $block.GetReader()

            $typeEntry = Find-ResourceEntry $reader ([int]($directory.RelativeVirtualAddress - $sectionBase)) 24
            $nameEntry = Find-ResourceEntry $reader ([int]($typeEntry -band 0x7FFFFFFF)) 1

            # The third level is the language entry; the manifest has exactly one (usually neutral).
            $nameOffset = [int]($nameEntry -band 0x7FFFFFFF)
            $reader.Offset = $nameOffset + 12
            $named = $reader.ReadUInt16()
            $ids = $reader.ReadUInt16()
            if (($named + $ids) -eq 0)
            {
                throw "${ExePath}: the manifest resource has no language entry"
            }
            $reader.Offset = $nameOffset + 16
            $null = $reader.ReadUInt32()
            $languageEntry = $reader.ReadUInt32()

            $reader.Offset = [int]($languageEntry -band 0x7FFFFFFF)
            $dataRva = $reader.ReadInt32()
            $size = $reader.ReadInt32()
            $content = $block.GetContent($dataRva - $sectionBase, $size)

            $bytes = [byte[]]::new($size)
            for ($i = 0; $i -lt $size; $i++)
            {
                $bytes[$i] = $content[$i]
            }

            $offset = 0
            if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
            {
                $offset = 3
            }
            return [System.Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
        }
        finally
        {
            $image.Dispose()
        }
    }
    finally
    {
        $stream.Dispose()
    }
}

function Find-ResourceEntry([System.Reflection.Metadata.BlobReader]$Reader, [int]$DirectoryOffset, [int]$Id)
{
    $Reader.Offset = $DirectoryOffset + 12 # past Characteristics, TimeDateStamp, Major/Minor
    $named = $Reader.ReadUInt16()
    $ids = $Reader.ReadUInt16()
    for ($i = 0; $i -lt ($named + $ids); $i++)
    {
        $Reader.Offset = $DirectoryOffset + 16 + ($i * 8)
        $name = $Reader.ReadUInt32()
        $dataOffset = $Reader.ReadUInt32()
        if (($name -band 0x80000000) -ne 0)
        {
            continue # a named entry; RT_MANIFEST and id 1 are numeric
        }
        if ($name -ne [uint32]$Id)
        {
            continue
        }
        return $dataOffset
    }
    throw "resource id $Id not found in the PE resource directory"
}

function Assert-Manifest([string]$ExePath, [string]$ExpectedUiAccess)
{
    $name = Split-Path -Leaf $ExePath
    $manifest = Get-EmbeddedManifestText $ExePath

    $expected = "uiAccess=`"$ExpectedUiAccess`""
    $forbidden = if ($ExpectedUiAccess -eq 'true') { 'uiAccess="false"' } else { 'uiAccess="true"' }

    if (-not $manifest.Contains($expected))
    {
        throw "$name must embed $expected but does not. Manifest: $manifest"
    }
    if ($manifest.Contains($forbidden))
    {
        throw "$name must not embed $forbidden. Manifest: $manifest"
    }
    if (-not $manifest.Contains('level="asInvoker"'))
    {
        throw "$name must stay asInvoker (never requireAdministrator). Manifest: $manifest"
    }
    Write-Note "$name manifest: $expected, asInvoker"
}

# ── Self-extraction refusal ──────────────────────────────────────────────────────────────────

function Assert-NoSelfExtraction([string]$ProjectPath, [bool]$UiAccess)
{
    $msbuildArguments = @(
        $ProjectPath, '-nologo',
        '-getProperty:IncludeNativeLibrariesForSelfExtract',
        '-getProperty:IncludeAllContentForSelfExtract',
        "-property:Configuration=$Configuration",
        '-property:PublishSingleFile=true',
        '-property:SelfContained=true',
        "-property:RuntimeIdentifier=$Runtime"
    )
    if ($UiAccess)
    {
        # Evaluate the same build the App publish performs: a self-extract property behind
        # Condition="'$(EyeCtrlUiAccess)' == 'true'" must not hide from this gate.
        $msbuildArguments += '-property:EyeCtrlUiAccess=true'
    }

    $json = & dotnet msbuild @msbuildArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw "dotnet msbuild could not evaluate $ProjectPath (exit $LASTEXITCODE)"
    }

    $text = $json -join "`n"
    $start = $text.IndexOf('{')
    $end = $text.LastIndexOf('}')
    if ($start -lt 0 -or $end -lt $start)
    {
        throw "dotnet msbuild returned no JSON for ${ProjectPath}:`n$text"
    }
    $properties = ($text.Substring($start, $end - $start + 1) | ConvertFrom-Json).Properties
    foreach ($name in @('IncludeNativeLibrariesForSelfExtract', 'IncludeAllContentForSelfExtract'))
    {
        $property = $properties.PSObject.Properties[$name]
        $value = if ($null -ne $property) { [string]$property.Value } else { '' }
        if ($value -and -not $value.Equals('false', [System.StringComparison]::OrdinalIgnoreCase))
        {
            throw ("Refusing to publish ${ProjectPath}: $name=$value would extract native libraries " +
                'into a user-writable %TEMP% and load them into a High-IL process. See the C9 plan section 4.')
        }
    }
    Write-Note "$(Split-Path -Leaf (Split-Path -Parent $ProjectPath)): no self-extraction"
}

# ── Publish and package ─────────────────────────────────────────────────────────────────────

function Invoke-Publish
{
    param(
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$OutDir,
        [switch]$UiAccess,
        [switch]$CompressSingleFile
    )

    $arguments = @(
        'publish', $ProjectPath, '--nologo',
        '-c', $Configuration,
        '-r', $Runtime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        # Embedded symbols keep file:line in the owner's stack traces inside the bundle, with no
        # loose PDB files beside the exe. XML docs are build artifacts and are not installed.
        '-p:DebugType=embedded',
        '-p:DebugSymbols=true',
        '-p:GenerateDocumentationFile=false',
        '-o', $OutDir
    )
    if ($UiAccess)
    {
        $arguments += '-p:EyeCtrlUiAccess=true'
    }
    if ($CompressSingleFile)
    {
        # Bundled assemblies decompress in memory; nothing is extracted to %TEMP%. The App stays
        # uncompressed so its start-up is not slowed.
        $arguments += '-p:EnableCompressionInSingleFile=true'
    }
    if ($Version)
    {
        $arguments += "-p:Version=$Version"
    }
    Invoke-Native 'dotnet' $arguments
}

function Copy-IntoPackage([string]$SourceDir, [string]$PackageDir)
{
    foreach ($file in Get-ChildItem -LiteralPath $SourceDir -Recurse -File)
    {
        $relative = [System.IO.Path]::GetRelativePath($SourceDir, $file.FullName)
        $target = Join-Path $PackageDir $relative
        if (Test-Path -LiteralPath $target)
        {
            $sourceHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($sourceHash -ne $targetHash)
            {
                throw "Package conflict: '$relative' differs between publish folders. Two projects cannot ship the same file name with different content."
            }
            continue
        }
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent))
        {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Copy-PackageTree([string]$SourceDir, [string]$TargetDir)
{
    foreach ($file in Get-ChildItem -LiteralPath $SourceDir -Recurse -File)
    {
        $relative = [System.IO.Path]::GetRelativePath($SourceDir, $file.FullName)
        $target = Join-Path $TargetDir $relative
        $parent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $parent))
        {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}

function Remove-PackageNoise([string]$PackageDir)
{
    $removed = 0
    foreach ($file in Get-ChildItem -LiteralPath $PackageDir -Recurse -File)
    {
        if ($PackageNoiseExtensions -contains $file.Extension -or $PackageNoiseNames -contains $file.Name)
        {
            Remove-Item -LiteralPath $file.FullName -Force
            $removed++
        }
    }
    Write-Note "pruned $removed build artifact(s) (*.pdb, *.lib, *.xml, README.md)"
}

# ── Signing ─────────────────────────────────────────────────────────────────────────────────

function Invoke-Sign([string]$FilePath, [string]$SignTool)
{
    Invoke-Native $SignTool @(
        'sign',
        '/fd', 'SHA256',
        '/sha1', $CertificateThumbprint,
        '/s', 'My',
        '/tr', $TimestampUrl,
        '/td', 'SHA256',
        $FilePath
    )
}

function Assert-Signed([string]$FilePath, [string]$SignTool)
{
    & $SignTool verify /pa $FilePath
    if ($LASTEXITCODE -ne 0)
    {
        throw "signtool verify /pa failed for $FilePath"
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid)
    {
        throw "$FilePath signature status is $($signature.Status), expected Valid"
    }
    if ($null -eq $signature.SignerCertificate)
    {
        throw "$FilePath has no signer certificate"
    }
    if (-not $signature.SignerCertificate.Thumbprint.Equals($CertificateThumbprint, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "$FilePath is signed by $($signature.SignerCertificate.Thumbprint), not the pinned $CertificateThumbprint"
    }
    Write-Note "$(Split-Path -Leaf $FilePath): signature Valid, pinned thumbprint"
}

# ── Package metadata and catalog ────────────────────────────────────────────────────────────

function Get-AppVersion([string]$ExePath)
{
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ExePath)
    $productVersion = $info.ProductVersion
    if ([string]::IsNullOrWhiteSpace($productVersion))
    {
        throw "$ExePath carries no product version; pass -Version"
    }
    return $productVersion.Trim()
}

function Assert-VersionFolderName([string]$Value)
{
    $invalid = [System.Collections.Generic.List[char]]::new([System.IO.Path]::GetInvalidFileNameChars())
    $invalid.Add([char]'/')
    $invalid.Add([char]'\')
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value.IndexOfAny($invalid.ToArray()) -ge 0)
    {
        throw "'$Value' is not a usable version folder name"
    }
}

function Write-PackageJson([string]$PackageDir, [string]$PackageVersion, [string]$Sha)
{
    $document = [ordered]@{
        version = $PackageVersion
        sha     = $Sha
        created = (Get-Date).ToUniversalTime().ToString('o')
    }
    $json = $document | ConvertTo-Json
    $path = Join-Path $PackageDir 'package.json'
    [System.IO.File]::WriteAllText($path, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))
    Write-Note "package.json: version=$PackageVersion sha=$Sha"
}

function New-PackageCatalog([string]$PackageDir)
{
    $catalogPath = Join-Path $PackageDir 'EyeCtrl.cat'
    if (Test-Path -LiteralPath $catalogPath)
    {
        Remove-Item -LiteralPath $catalogPath -Force
    }
    New-FileCatalog -Path $PackageDir -CatalogFilePath $catalogPath -CatalogVersion 2 | Out-Null
    if (-not (Test-Path -LiteralPath $catalogPath))
    {
        throw "New-FileCatalog did not produce $catalogPath"
    }
    Write-Note "EyeCtrl.cat: New-FileCatalog v2 (SHA-256, SIP member tags)"
}

function Assert-CatalogValid([string]$PackageDir)
{
    $catalogPath = Join-Path $PackageDir 'EyeCtrl.cat'
    # Hash-only: every non-catalog file must be a member and unmodified. package.json is one of
    # them, because it is written before the catalog.
    $status = Test-FileCatalog -Path $PackageDir -CatalogFilePath $catalogPath -ErrorAction Stop
    if ("$status" -ne 'Valid')
    {
        throw "Test-FileCatalog reports $status for $PackageDir"
    }
    Write-Note "Test-FileCatalog: Valid"
}

# ── Main ────────────────────────────────────────────────────────────────────────────────────

$repoRoot = Get-RepoRoot
$appProject = Join-Path $repoRoot 'src/EyeCtrl.App/EyeCtrl.App.csproj'
$supervisorProject = Join-Path $repoRoot 'src/EyeCtrl.Supervisor/EyeCtrl.Supervisor.csproj'
$setupProject = Join-Path $repoRoot 'src/EyeCtrl.Setup/EyeCtrl.Setup.csproj'
$inboxRoot = Join-Path $env:LOCALAPPDATA 'EyeCtrl\updates'

if (-not (Test-Path -LiteralPath $appProject))
{
    throw "App project not found at $appProject"
}
if (-not (Test-Path -LiteralPath $supervisorProject))
{
    throw "Supervisor project not found at $supervisorProject"
}

if ($DryRun -and -not $NoSign)
{
    $NoSign = $true
    Write-Note 'DryRun implies NoSign: nothing is signed and the inbox is not written'
}

if ($Version)
{
    if ($Version.Contains('+'))
    {
        throw ("-Version must be a plain numeric version: MSBuild's Version property cannot carry " +
            'build metadata. The App informational version already appends +<sha>.')
    }
    Assert-VersionFolderName $Version
}

if (-not $ScratchDir)
{
    $ScratchDir = Join-Path ([System.IO.Path]::GetTempPath()) ('eyectrl-publish-' + (Get-Date).ToString('yyyyMMdd-HHmmss'))
}
$ScratchDir = [System.IO.Path]::GetFullPath($ScratchDir)
$packageDir = Join-Path $ScratchDir 'package'
$buildRoot = Join-Path $ScratchDir 'build'

function Assert-NotInInbox([string]$Path, [string]$Why)
{
    $full = [System.IO.Path]::GetFullPath($Path).TrimEnd('\')
    $inbox = [System.IO.Path]::GetFullPath($inboxRoot).TrimEnd('\')
    if ($full.Equals($inbox, [System.StringComparison]::OrdinalIgnoreCase) -or
        $full.StartsWith($inbox + '\', [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "Refusing to write '$Path' under the real inbox ($inbox): $Why"
    }
}

Write-Step "EyeCtrl signed publish"
Write-Note "repo:      $repoRoot"
Write-Note "scratch:   $ScratchDir"
Write-Note "config:    $Configuration / $Runtime"
Write-Note "signing:   $(if ($NoSign) { 'OFF (NoSign)' } else { 'ON, certificate ' + $CertificateThumbprint })"
Write-Note "setup:     $(if (Test-Path -LiteralPath $setupProject) { 'present' } else { 'absent on this branch' })"

$sha = $null
Push-Location $repoRoot
try
{
    $sha = (& git rev-parse HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sha))
    {
        $sha = $env:EYECTRL_BUILD_SHA
    }
}
finally
{
    Pop-Location
}
if ([string]::IsNullOrWhiteSpace($sha))
{
    $sha = 'unknown'
    Write-WarningLine 'Could not read the git SHA; package.json will say "unknown".'
}
$sha = $sha.Trim()
Write-Note "sha:       $sha"

if (-not $NoSign)
{
    # package.json's sha claims the sources this package was built from, and the package is signed.
    # A dirty tree makes that claim false, so a signing publish refuses it outright.
    Push-Location $repoRoot
    try
    {
        $dirty = (& git status --porcelain 2>$null)
        if ($LASTEXITCODE -eq 0 -and $dirty)
        {
            $changed = (@($dirty) | Select-Object -First 10) -join [Environment]::NewLine
            throw ("The working tree is dirty: package.json's sha names HEAD ($sha), not the built " +
                "sources. Commit or stash before a signing publish, or use -DryRun -NoSign.`n$changed")
        }
    }
    finally
    {
        Pop-Location
    }
}

Write-Step 'Refusing self-extraction'
Assert-NoSelfExtraction $appProject $true
Assert-NoSelfExtraction $supervisorProject $false

Write-Step 'Publishing'
if (Test-Path -LiteralPath $packageDir)
{
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

$appOut = Join-Path $buildRoot 'app'
$supervisorOut = Join-Path $buildRoot 'supervisor'
$setupOut = Join-Path $buildRoot 'setup'
# dotnet publish does not clear -o, so a reused scratch folder would carry stale files into the
# signed package. Start each publish output empty.
foreach ($publishOut in @($appOut, $supervisorOut, $setupOut))
{
    if (Test-Path -LiteralPath $publishOut)
    {
        Remove-Item -LiteralPath $publishOut -Recurse -Force
    }
}
Invoke-Publish -ProjectPath $appProject -OutDir $appOut -UiAccess
Invoke-Publish -ProjectPath $supervisorProject -OutDir $supervisorOut -CompressSingleFile

$haveSetup = Test-Path -LiteralPath $setupProject
if ($haveSetup)
{
    Assert-NoSelfExtraction $setupProject $false
    Invoke-Publish -ProjectPath $setupProject -OutDir $setupOut
}
elseif (-not $SkipSetup -and -not $NoSign)
{
    throw ("$setupProject does not exist, so the package could not install itself. " +
        'Pass -SkipSetup for an App + Supervisor package, or publish from a branch that has S6a.')
}
else
{
    Write-WarningLine 'src/EyeCtrl.Setup is absent on this branch: the package holds App + Supervisor only and is NOT installable. This is expected in a dry run before S6a merges.'
}

$appExe = Join-Path $appOut 'EyeCtrl.App.exe'
$supervisorExe = Join-Path $supervisorOut 'EyeCtrl.Supervisor.exe'
foreach ($required in @($appExe, $supervisorExe))
{
    if (-not (Test-Path -LiteralPath $required))
    {
        throw "Publish did not produce $required"
    }
}
if (Test-Path -LiteralPath (Join-Path $appOut 'EyeCtrl.App.dll'))
{
    throw 'The App publish folder contains EyeCtrl.App.dll: single-file publish did not bundle the app.'
}

Write-Step 'Checking the embedded manifests'
Assert-Manifest $appExe 'true'
Assert-Manifest $supervisorExe 'false'
if ($haveSetup)
{
    $setupExe = Join-Path $setupOut $SetupExeName
    if (-not (Test-Path -LiteralPath $setupExe))
    {
        throw "Publish did not produce $setupExe"
    }
    Assert-Manifest $setupExe 'false'
}

Write-Step 'Assembling the package'
Copy-IntoPackage $appOut $packageDir
Copy-IntoPackage $supervisorOut $packageDir
if ($haveSetup)
{
    Copy-IntoPackage $setupOut $packageDir
}
Remove-PackageNoise $packageDir

$packagedApp = Join-Path $packageDir 'EyeCtrl.App.exe'
foreach ($exeName in $RequiredPackageExes)
{
    if (-not (Test-Path -LiteralPath (Join-Path $packageDir $exeName)))
    {
        throw "The package is missing $exeName"
    }
}
if ($haveSetup -and -not (Test-Path -LiteralPath (Join-Path $packageDir $SetupExeName)))
{
    throw "The package is missing $SetupExeName"
}

# The natives stay loose beside the single-file exe (never self-extracted). A publish that lost
# them is the silent self-extraction failure section 4 warns about.
foreach ($native in $RequiredNativeDlls)
{
    if (-not (Test-Path -LiteralPath (Join-Path $packageDir $native)))
    {
        throw "The package is missing the loose native ${native}: the single-file publish may be self-extracting, or the App was built without it."
    }
}

# The folder, package.json and the App's own informational version must agree. -Version only
# changes the numeric part; the App always reports <version>+<sha>.
$packageVersion = Get-AppVersion $packagedApp
Assert-VersionFolderName $packageVersion
Write-Note "version:   $packageVersion"

function Resolve-Destination
{
    if ($OutputDir)
    {
        return [System.IO.Path]::GetFullPath($OutputDir)
    }
    if ($DryRun)
    {
        return $packageDir
    }
    return (Join-Path $inboxRoot $packageVersion)
}

$destination = Resolve-Destination
if ($NoSign)
{
    Assert-NotInInbox $destination 'an unsigned package must never appear in the update inbox'
}
if (-not $haveSetup)
{
    Assert-NotInInbox $destination 'a package without EyeCtrl.Setup cannot install itself'
}

Write-Step 'Signing'
if ($NoSign)
{
    Write-WarningLine 'NoSign: no exe and no catalog is signed. This package is NOT installable.'
}
else
{
    $signTool = Resolve-SignTool $SignToolPath
    Write-Note "signtool:  $signTool"

    $exes = @(Get-ChildItem -LiteralPath $packageDir -File -Filter '*.exe' | Sort-Object Name)
    foreach ($exe in $exes)
    {
        Write-Step "Signing $($exe.Name)"
        Invoke-Sign $exe.FullName $signTool
        Assert-Signed $exe.FullName $signTool
    }
}

Write-Step 'Writing package.json and the catalog'
Write-PackageJson $packageDir $packageVersion $sha
New-PackageCatalog $packageDir
Assert-CatalogValid $packageDir

if ($NoSign)
{
    Write-Note 'EyeCtrl.cat left unsigned (NoSign): hash-valid, but no signature to verify.'
}
else
{
    Write-Step 'Signing EyeCtrl.cat'
    Invoke-Sign (Join-Path $packageDir 'EyeCtrl.cat') $signTool
    Assert-Signed (Join-Path $packageDir 'EyeCtrl.cat') $signTool
    Assert-CatalogValid $packageDir
}

Write-Step 'Delivering the package'
if ($destination -eq $packageDir)
{
    Write-Note "left in place: $packageDir"
}
else
{
    $versionName = Split-Path -Leaf $destination
    if ((Test-Path -LiteralPath $destination) -and -not $Force)
    {
        throw "$destination already exists. Bump the version, or pass -Force to replace it."
    }

    # Atomic publish: the wrapper (and Setup) must never see a half-copied package, and the real
    # name must never be missing. Copy into a dot-prefixed sibling and validate the catalog there;
    # only then move the previous folder aside, rename the validated one into place, and delete it.
    $parent = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $parent))
    {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $partial = Join-Path $parent ('.' + $versionName + '.partial')
    if (Test-Path -LiteralPath $partial)
    {
        Remove-Item -LiteralPath $partial -Recurse -Force
    }
    New-Item -ItemType Directory -Path $partial -Force | Out-Null
    Copy-PackageTree $packageDir $partial
    Assert-CatalogValid $partial

    # -Force over the same version: the old folder is RENAMED aside, never deleted under its real
    # name, so updates\<version> holds a complete package at every moment.
    $replacedPrevious = $false
    if (Test-Path -LiteralPath $destination)
    {
        $oldName = '.' + $versionName + '.old'
        $oldPath = Join-Path $parent $oldName
        if (Test-Path -LiteralPath $oldPath)
        {
            Remove-Item -LiteralPath $oldPath -Recurse -Force
        }
        Rename-Item -LiteralPath $destination -NewName $oldName
        $replacedPrevious = $true
    }

    Rename-Item -LiteralPath $partial -NewName $versionName
    Write-Note "copied to: $destination"
    Assert-CatalogValid $destination

    if ($replacedPrevious)
    {
        Remove-Item -LiteralPath (Join-Path $parent ('.' + $versionName + '.old')) -Recurse -Force
        Write-Note "replaced the previous $versionName"
    }
}

# ── Report ──────────────────────────────────────────────────────────────────────────────────

Write-Step 'Package'
$files = @(Get-ChildItem -LiteralPath $destination -Recurse -File | Sort-Object Length -Descending)
$totalBytes = if ($files.Count -gt 0) { ($files | Measure-Object -Property Length -Sum).Sum } else { 0 }
Write-Host ("   {0} files, {1:N1} MB total" -f $files.Count, ($totalBytes / 1MB))
foreach ($file in $files)
{
    $relative = [System.IO.Path]::GetRelativePath($destination, $file.FullName)
    Write-Host ("   {0,10:N1} MB  {1}" -f ($file.Length / 1MB), $relative)
}

Write-Step 'Result'
Write-Host "   version:    $packageVersion"
Write-Host "   sha:        $sha"
Write-Host "   package:    $destination"
Write-Host "   signed:     $(if ($NoSign) { 'no (NoSign)' } else { 'yes, every exe and EyeCtrl.cat' })"
Write-Host "   inbox:      $(if ($destination.StartsWith([System.IO.Path]::GetFullPath($inboxRoot), [System.StringComparison]::OrdinalIgnoreCase)) { 'written' } else { 'not written' })"
if ($NoSign)
{
    Write-Host ''
    Write-WarningLine 'Dry/NoSign package complete. Nothing was signed, nothing was installed, no exe was run.'
}
