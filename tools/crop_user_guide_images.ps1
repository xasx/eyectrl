param(
    # Source directory holding the untracked 2026-09-22 tour captures (3840px full-screen
    # PNGs). These are NOT committed to the repo - they live in the main checkout under
    # visual-assessment\shots. Pass -SourceDir explicitly when running from a worktree.
    [string]$SourceDir = "",
    [int]$TargetWidth = 1600
)

$repoRoot = Split-Path -Parent $PSScriptRoot
$dst = Join-Path $repoRoot 'docs\images'
if (-not $SourceDir) { $SourceDir = Join-Path $repoRoot 'visual-assessment\shots' }
if (-not (Test-Path $SourceDir)) {
    Write-Error "Source captures not found under '$SourceDir'. They are untracked tour byproducts that exist only in the main checkout - pass -SourceDir <main-checkout>\visual-assessment\shots."
    exit 1
}
New-Item -ItemType Directory -Force -Path $dst | Out-Null

Add-Type -AssemblyName System.Drawing

# name, source, crop (x,y,w,h in 3840px source pixels).
# RightTrim crops the right ~500px off the keyboard shots: the 2026-09-22 tour captured
# them with the action menu's stale Close+Help edge pair on screen (an icon-only
# Close+Flip pair is current), so the edge bar is cut out of the published images.
$jobs = @(
    @{ out = "toolbar.png";          src = "tour01_toolbar.png";          x = 0; y = 0; w = 3840; h = 400;  trim = 0 },
    @{ out = "dwell-arc.png";        src = "tour05_dwellarc_mid.png";     x = 0; y = 0; w = 3840; h = 600;  trim = 0 },
    @{ out = "keyboard-letters.png"; src = "tour02_keyboard_initial.png"; x = 0; y = 0; w = 3840; h = 1500; trim = 500 },
    @{ out = "keyboard-fn.png";      src = "tour02_function_settled.png"; x = 0; y = 0; w = 3840; h = 1600; trim = 500 },
    @{ out = "keyboard-emoji.png";   src = "tour02_emoji_settled.png";    x = 0; y = 0; w = 3840; h = 1500; trim = 500 }
)

foreach ($j in $jobs) {
    $img = [System.Drawing.Bitmap]::FromFile((Join-Path $SourceDir $j.src))
    $w = $j.w - $j.trim
    $rect = New-Object System.Drawing.Rectangle($j.x, $j.y, $w, $j.h)
    $crop = $img.Clone($rect, $img.PixelFormat)
    $scale = $TargetWidth / $crop.Width
    $th = [int]([Math]::Round($crop.Height * $scale))
    $res = New-Object System.Drawing.Bitmap($TargetWidth, $th)
    $g = [System.Drawing.Graphics]::FromImage($res)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($crop, 0, 0, $TargetWidth, $th)
    $g.Dispose()
    $outPath = Join-Path $dst $j.out
    $res.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose(); $img.Dispose(); $res.Dispose()
    Write-Output ("{0}: {1}x{2} (trimmed right {3}px)" -f $j.out, $TargetWidth, $th, $j.trim)
}
