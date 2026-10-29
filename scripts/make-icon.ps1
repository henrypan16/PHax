<#
.SYNOPSIS
  Converts a PNG logo into a multi-resolution Windows icon (16-256 px) and a 256 px PNG.
  The logo is centred on a transparent square canvas so non-square images are not distorted.
.EXAMPLE
  .\scripts\make-icon.ps1 -Logo .\logo.png -Output .\PHax.App\Assets\PHax.ico
#>
param(
    [string]$Logo = (Join-Path $PSScriptRoot "..\logo.png"),
    [string]$Output = (Join-Path $PSScriptRoot "..\PHax.App\Assets\PHax.ico"),
    [int[]]$Sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
)
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$Logo = (Resolve-Path $Logo).Path
$source = [System.Drawing.Image]::FromFile($Logo)
try {
    $side = [Math]::Max($source.Width, $source.Height)
    $images = @()
    foreach ($size in ($Sizes | Sort-Object)) {
        $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.Clear([System.Drawing.Color]::Transparent)
        $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $scale = $size / $side
        $w = $source.Width * $scale
        $h = $source.Height * $scale
        $g.DrawImage($source, [single](($size - $w) / 2), [single](($size - $h) / 2), [single]$w, [single]$h)
        $g.Dispose()
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($size -eq 256) { $bmp.Save([System.IO.Path]::ChangeExtension($Output, ".png"), [System.Drawing.Imaging.ImageFormat]::Png) }
        $bmp.Dispose()
        $images += , @{ Size = $size; Data = $ms.ToArray() }
        $ms.Dispose()
    }

    # Installer wizard header image (24-bit BMP on white; Inno Setup scales it).
    $wiz = New-Object System.Drawing.Bitmap 110, 110, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($wiz)
    $g.Clear([System.Drawing.Color]::White)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $scale = 104 / $side
    $w = $source.Width * $scale; $h = $source.Height * $scale
    $g.DrawImage($source, [single]((110 - $w) / 2), [single]((110 - $h) / 2), [single]$w, [single]$h)
    $g.Dispose()
    $wiz.Save((Join-Path (Split-Path $Output) "wizard-small.bmp"), [System.Drawing.Imaging.ImageFormat]::Bmp)
    $wiz.Dispose()
}
finally {
    $source.Dispose()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
New-Item -ItemType Directory -Force (Split-Path $Output) | Out-Null
$fs = [System.IO.File]::Create($Output)
$bw = New-Object System.IO.BinaryWriter $fs
try {
    $bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
        $bw.Write([UInt16]1); $bw.Write([UInt16]32)
        $bw.Write([UInt32]$img.Data.Length); $bw.Write([UInt32]$offset)
        $offset += $img.Data.Length
    }
    foreach ($img in $images) { $bw.Write($img.Data) }
}
finally {
    $bw.Dispose()
}
Write-Host "Icon written: $Output ($($images.Count) sizes)"
