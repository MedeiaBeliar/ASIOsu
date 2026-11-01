$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class IconNative { [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle); }
'@
$output=Join-Path $PSScriptRoot '..\app\ASIOsu\Assets\ASIOsu.ico'
New-Item -ItemType Directory -Path (Split-Path $output) -Force | Out-Null
$bitmap=[Drawing.Bitmap]::new(64,64,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics=[Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode=[Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([Drawing.Color]::Transparent)
$pen=New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255,15,108,189)),6
$brush=New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,15,108,189))
try {
    $graphics.DrawEllipse($pen,7,7,50,50)
    $graphics.FillEllipse($brush,25,25,14,14)
    $handle=$bitmap.GetHicon()
    try {
        $icon=[Drawing.Icon]::FromHandle($handle)
        $stream=[IO.File]::Create($output)
        try {$icon.Save($stream)}finally{$stream.Dispose();$icon.Dispose()}
    } finally {[IconNative]::DestroyIcon($handle)|Out-Null}
} finally {$brush.Dispose();$pen.Dispose();$graphics.Dispose();$bitmap.Dispose()}
Write-Output "PASS: icon generated at $output"

