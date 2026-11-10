# Draws the launcher icon (blue rounded square with a white lightning bolt)
# and writes src/app.ico with PNG frames 16..256 px.
# Usage: powershell -ExecutionPolicy Bypass -File tools/make_icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'src\app.ico'
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256

function Draw([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $m = if ($n -le 24) { 0 } else { $n * 0.04 }
    $s = $n - 2 * $m
    $r = $s * 0.24
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($m + $s - 2 * $r, $m, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($m + $s - 2 * $r, $m + $s - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc($m, $m + $s - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $p0 = New-Object System.Drawing.PointF $m, $m
    $p1 = New-Object System.Drawing.PointF ($m + $s), ($m + $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush $p0, $p1, ([System.Drawing.Color]::FromArgb(255, 0x4F, 0x8D, 0xF7)), ([System.Drawing.Color]::FromArgb(255, 0x1D, 0x3F, 0xC4))
    $g.FillPath($bg, $path)

    # lightning bolt, coordinates in 0..1 of the square
    $bolt = @(@(0.57, 0.11), @(0.25, 0.56), @(0.46, 0.56), @(0.40, 0.89), @(0.76, 0.42), @(0.55, 0.42), @(0.66, 0.11))
    $pts = foreach ($q in $bolt) { New-Object System.Drawing.PointF ($m + $q[0] * $s), ($m + $q[1] * $s) }
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$frames = foreach ($n in $sizes) { , (Draw $n) }
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $len = $frames[$i].Length
    $d = if ($n -ge 256) { 0 } else { $n }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Close()
# preview for the README / review
[System.IO.File]::WriteAllBytes((Join-Path $root 'docs\icon_256.png'), $frames[$sizes.Count - 1])
"wrote $out ($((Get-Item $out).Length) bytes)"
