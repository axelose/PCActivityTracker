$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class AssetNativeMethods {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@

function New-LogoBitmap([int]$width, [int]$height) {
    $bitmap = New-Object System.Drawing.Bitmap($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.Clear([System.Drawing.Color]::FromArgb(24, 32, 57))
    $accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(91, 92, 226))
    $diameter = [Math]::Min($width, $height) * 0.72
    $graphics.FillEllipse($accent, ($width - $diameter) / 2, ($height - $diameter) / 2, $diameter, $diameter)
    $fontSize = [Math]::Max(7, [Math]::Min($width, $height) * 0.28)
    $font = New-Object System.Drawing.Font('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $textBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $format = New-Object System.Drawing.StringFormat
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Center
    $graphics.DrawString('PC', $font, $textBrush, (New-Object System.Drawing.RectangleF(0, 0, $width, $height)), $format)
    $format.Dispose(); $textBrush.Dispose(); $font.Dispose(); $accent.Dispose(); $graphics.Dispose()
    return $bitmap
}

function Save-Png([int]$width, [int]$height, [string]$path) {
    $bitmap = New-LogoBitmap $width $height
    try { $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}

$assets = Join-Path $root 'assets'
$extensionIcons = Join-Path $root 'browser-extension\icons'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
New-Item -ItemType Directory -Path $extensionIcons -Force | Out-Null
Save-Png 50 50 (Join-Path $assets 'StoreLogo.png')
Save-Png 150 150 (Join-Path $assets 'Square150x150Logo.png')
Save-Png 44 44 (Join-Path $assets 'Square44x44Logo.png')
Save-Png 310 150 (Join-Path $assets 'Wide310x150Logo.png')
foreach ($size in @(16,32,48,128)) { Save-Png $size $size (Join-Path $extensionIcons "icon$size.png") }

$iconBitmap = New-LogoBitmap 256 256
$handle = $iconBitmap.GetHicon()
try {
    $icon = [System.Drawing.Icon]::FromHandle($handle)
    $stream = [System.IO.File]::Create((Join-Path $assets 'PCActivityTracker.ico'))
    try { $icon.Save($stream) } finally { $stream.Dispose(); $icon.Dispose() }
} finally {
    $iconBitmap.Dispose()
    [AssetNativeMethods]::DestroyIcon($handle) | Out-Null
}

Write-Host "Generated application and extension assets in $assets and $extensionIcons"
