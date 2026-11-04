#Requires -Version 7.0
<#
.SYNOPSIS
    Starts the newest published EyeCtrl package's Setup, to update an installed EyeCtrl.

.DESCRIPTION
    A thin wrapper with no install logic of its own. It finds the newest package in
    %LOCALAPPDATA%\EyeCtrl\updates\<version>\ (newest by the package.json "created" stamp, then
    version, then folder name) and starts that package's EyeCtrl.Setup.exe with the arguments
    "update <packageDir>".

    It always starts Setup with Start-Process (ShellExecute), never with the PowerShell call
    operator: a plain CreateProcess of a launched executable is what fails with Win32 error 740
    for a UIAccess image. Setup itself is asInvoker and medium, so this matters for the contract,
    not for today's token.

    Dot-prefixed folders are skipped: Publish-Signed.ps1 assembles an inbox package in
    .<version>.partial and only renames it into place once its catalog validates.

    Nothing is downloaded, verified, copied or installed here: Setup does all of that. This
    wrapper never writes to Program Files, the registry or %APPDATA%; it only starts a process.

    Only run this on the owner's go. Dev and CI never install anything.

.PARAMETER PackageDir
    A specific package folder to start. Default: the newest valid package in -InboxRoot.

.PARAMETER InboxRoot
    The update inbox. Default: %LOCALAPPDATA%\EyeCtrl\updates.

.PARAMETER UntilExit
    Block until Setup exits and return its exit code. It waits for Setup only, never for the
    process tree it starts: the App that Setup launches outlives it by design.

.EXAMPLE
    ./Install-EyeCtrl.ps1 -WhatIf

.EXAMPLE
    ./Install-EyeCtrl.ps1 -UntilExit
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$PackageDir,
    [string]$InboxRoot,
    [switch]$UntilExit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$SetupExeName = 'EyeCtrl.Setup.exe'
$SetupVerb = 'update'

if (-not $InboxRoot)
{
    $InboxRoot = Join-Path $env:LOCALAPPDATA 'EyeCtrl\updates'
}

function Resolve-PackagePath([string]$Path)
{
    # GetFullPath('C:\x\') keeps the trailing separator, and CommandLineToArgvW reads the
    # resulting ...update "C:\x\" as a literal quote inside the argument.
    return [System.IO.Path]::TrimEndingDirectorySeparator([System.IO.Path]::GetFullPath($Path))
}

function Write-SetupRecordOutcome([datetimeoffset]$NotBefore)
{
    # S6c D2: rolledBack and degraded exit 0 by design, so the exit code alone is not the answer.
    # Print the record this run wrote (a record older than NotBefore is from an earlier run).
    $recordPath = Join-Path $env:APPDATA 'EyeCtrl\setup-last.json'
    if (-not (Test-Path -LiteralPath $recordPath))
    {
        Write-Host 'No setup-last.json was written; the exit code is the only outcome.'
        return
    }

    try
    {
        $record = Get-Content -LiteralPath $recordPath -Raw | ConvertFrom-Json
    }
    catch
    {
        Write-Host 'setup-last.json could not be read; the exit code is the only outcome.'
        return
    }

    $recordTime = [datetimeoffset]::MinValue
    $timeProperty = $record.PSObject.Properties['time']
    if ($null -ne $timeProperty)
    {
        $parsed = [datetimeoffset]::MinValue
        if ([datetimeoffset]::TryParse([string]$timeProperty.Value, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$parsed))
        {
            $recordTime = $parsed
        }
    }

    if ($recordTime -lt $NotBefore)
    {
        Write-Host 'setup-last.json is older than this run; the exit code is the only outcome.'
        return
    }

    Write-Host "Setup outcome: $($record.outcome) (version $($record.version), previous $($record.previousVersion), detail $($record.detail))."
}

function Read-PackageStamp([string]$PackagePath)
{
    $manifestPath = Join-Path $PackagePath 'package.json'
    if (-not (Test-Path -LiteralPath $manifestPath))
    {
        return $null
    }
    try
    {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    }
    catch
    {
        Write-Warning "Skipping '$PackagePath': package.json is not valid JSON ($($_.Exception.Message))."
        return $null
    }

    $versionProperty = $manifest.PSObject.Properties['version']
    $createdProperty = $manifest.PSObject.Properties['created']
    $version = if ($null -ne $versionProperty) { [string]$versionProperty.Value } else { '' }

    # "1.2.3", "1.2.3+<sha>" and "1.2.3-rc1" all compare on their numeric core. The created stamp
    # is the primary order; the version and the folder name are the tie-breaks.
    $versionKey = $null
    if (-not [version]::TryParse(($version -split '[+\-]')[0], [ref]$versionKey))
    {
        $versionKey = [version]'0.0.0'
    }

    $created = [datetimeoffset]::MinValue
    if ($null -ne $createdProperty)
    {
        # ConvertFrom-Json turns an ISO date string into a DateTime (PowerShell 7.5+), so accept
        # both shapes instead of re-parsing a culture-formatted string.
        $createdValue = $createdProperty.Value
        if ($createdValue -is [datetimeoffset])
        {
            $created = $createdValue
        }
        elseif ($createdValue -is [datetime])
        {
            $created = [datetimeoffset]$createdValue
        }
        else
        {
            $parsed = [datetimeoffset]::MinValue
            if ([datetimeoffset]::TryParse([string]$createdValue, [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$parsed))
            {
                $created = $parsed
            }
        }
    }
    return [pscustomobject]@{
        Path       = $PackagePath
        Name       = Split-Path -Leaf $PackagePath
        Version    = $version
        VersionKey = $versionKey
        Created    = $created
    }
}

if (-not $PackageDir)
{
    if (-not (Test-Path -LiteralPath $InboxRoot))
    {
        throw "The update inbox '$InboxRoot' does not exist. Publish a package first (tools/release/Publish-Signed.ps1)."
    }

    $candidates = Get-ChildItem -LiteralPath $InboxRoot -Directory |
        Where-Object { -not $_.Name.StartsWith('.') } |
        ForEach-Object { Read-PackageStamp $_.FullName } |
        Where-Object { $null -ne $_ -and (Test-Path -LiteralPath (Join-Path $_.Path $SetupExeName)) }

    $newest = $candidates |
        Sort-Object -Property @{ Expression = 'Created'; Descending = $true },
            @{ Expression = 'VersionKey'; Descending = $true },
            @{ Expression = 'Name'; Descending = $true } |
        Select-Object -First 1

    if ($null -eq $newest)
    {
        throw "No package in '$InboxRoot' holds both package.json and $SetupExeName."
    }
    $PackageDir = $newest.Path
    Write-Host "Newest package: version $($newest.Version), created $($newest.Created.ToString('u'))"
}
else
{
    $PackageDir = Resolve-PackagePath $PackageDir
    if (-not (Test-Path -LiteralPath $PackageDir))
    {
        throw "Package folder '$PackageDir' does not exist."
    }
}

$PackageDir = Resolve-PackagePath $PackageDir
$setupExe = Join-Path $PackageDir $SetupExeName
if (-not (Test-Path -LiteralPath $setupExe))
{
    throw "'$PackageDir' has no $SetupExeName. A package without Setup cannot install itself."
}

# Quoted because the package path is under %LOCALAPPDATA% and may carry spaces.
$setupArguments = '{0} "{1}"' -f $SetupVerb, $PackageDir
$target = "$setupExe $setupArguments"

if (-not $PSCmdlet.ShouldProcess($target, 'Start Setup'))
{
    return
}

Write-Host "Starting: $setupExe"
Write-Host "Arguments: $setupArguments"

# Start-Process is ShellExecute here (no window suppression, no redirection), which is the
# launch path the C9 plan measured. Never replace this with the call operator.
if ($UntilExit)
{
    # -PassThru plus WaitForExit waits for Setup itself. Start-Process's own wait flag would wait
    # for the whole process tree, and the App that Setup starts outlives it by hours.
    $startedAt = [datetimeoffset]::UtcNow
    $process = Start-Process -FilePath $setupExe -ArgumentList $setupArguments -WorkingDirectory $PackageDir -PassThru
    $process.WaitForExit()
    Write-Host "Setup exited with code $($process.ExitCode)."
    Write-SetupRecordOutcome -NotBefore $startedAt
    exit $process.ExitCode
}

$started = Start-Process -FilePath $setupExe -ArgumentList $setupArguments -WorkingDirectory $PackageDir -PassThru
Write-Host "Setup started (pid $($started.Id)). It shows its own progress window; nothing to do."
