param()
Add-Type -AssemblyName System.Drawing

$src = "C:\Users\andre\source\repos\eyectrl\visual-assessment\shots"
$dst = "C:\Users\andre\source\repos\eyectrl\docs\images"
New-Item -ItemType Directory -Force -Path $dst | Out-Null

# name, source, crop (x,y,w,h), target width
$jobs = @(
    @{ out = "toolbar.png";         src = "tour01_toolbar.png";           x = 0; y = 0;    w = 3840; h = 400;  tw = 1600 },
    @{ out = "dwell-arc.png";       src = "tour05_dwellarc_mid.png";      x = 0; y = 0;    w = 3840; h = 600;  tw = 1600 },
    @{ out = "keyboard-letters.png";src = "tour02_keyboard_initial.png";  x = 0; y = 0;    w = 3840; h = 1500; tw = 1600 },
    @{ out = "keyboard-fn.png";     src = "tour02_function_settled.png";  x = 0; y = 0;    w = 3840; h = 1600; tw = 1600 },
    @{ out = "keyboard-emoji.png";  src = "tour02_emoji_settled.png";     x = 0; y = 0;    w = 3840; h = 1500; tw = 1600 }
)

foreach ($j in $jobs) {
    $img = [System.Drawing.Bitmap]::FromFile((Join-Path $src $j.src))
    $rect = New-Object System.Drawing.Rectangle($j.x, $j.y, $j.w, $j.h)
    $crop = $img.Clone($rect, $img.PixelFormat)
    $scale = $j.tw / $crop.Width
    $th = [int]([Math]::Round($crop.Height * $scale))
    $res = New-Object System.Drawing.Bitmap($j.tw, $th)
    $g = [System.Drawing.Graphics]::FromImage($res)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($crop, 0, 0, $j.tw, $th)
    $g.Dispose()
    $outPath = Join-Path $dst $j.out
    $res.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $crop.Dispose(); $img.Dispose(); $res.Dispose()
    Write-Output ("{0}: {1}x{2}" -f $j.out, $j.tw, $th)
}
