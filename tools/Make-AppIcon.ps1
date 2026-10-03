<#
.SYNOPSIS
  Generates src\NetRoute.App\Assets\NetRouteWidget.ico: the tray globe (Phone green) at 16..256 px.
.DESCRIPTION
  Same geometry as TrayIcon.Draw (32 px grid: filled circle 2,2,28,28; meridian ellipse 10,3,12,26; equator 3,16 to 29,16;
  white 2 px lines), scaled per size. -PreviewPng also writes the 256 px image so it can be looked at.
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\src\NetRoute.App\Assets\NetRouteWidget.ico'),
    [string]$PreviewPng
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256

function New-GlobePng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 32.0
    $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x2E, 0xA0, 0x43))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.0, 2 * $k))
    $g.FillEllipse($brush, [single](2 * $k), [single](2 * $k), [single](28 * $k), [single](28 * $k))
    $g.DrawEllipse($pen, [single](10 * $k), [single](3 * $k), [single](12 * $k), [single](26 * $k))
    $g.DrawLine($pen, [single](3 * $k), [single](16 * $k), [single](29 * $k), [single](16 * $k))
    $g.Dispose(); $brush.Dispose(); $pen.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$images = foreach ($s in $sizes) { [pscustomobject]@{ Size = $s; Png = (New-GlobePng $s) } }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$stream = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $stream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)       # ICONDIR: reserved, type 1 = icon, count
$offset = 6 + 16 * $images.Count
foreach ($i in $images) {
    $dim = if ($i.Size -ge 256) { [byte]0 } else { [byte]$i.Size }                   # 0 means 256
    $w.Write($dim); $w.Write($dim); $w.Write([byte]0); $w.Write([byte]0)             # width, height, colours, reserved
    $w.Write([uint16]1); $w.Write([uint16]32)                                         # planes, bits per pixel
    $w.Write([uint32]$i.Png.Length); $w.Write([uint32]$offset)                        # image size, offset
    $offset += $i.Png.Length
}
foreach ($i in $images) { $w.Write($i.Png) }
$w.Flush(); $w.Dispose(); $stream.Dispose()
if ($PreviewPng) { [System.IO.File]::WriteAllBytes($PreviewPng, ($images | Where-Object Size -eq 256).Png) }
Write-Host "Wrote $Out ($($images.Count) sizes)"
