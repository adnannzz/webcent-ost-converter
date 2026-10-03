# Renders the Webcent OST Converter logo (assets/brand/webcent-source.svg) into PNG and ICO files.
# Run with Windows PowerShell:  powershell -STA -File tools/make-icons.ps1
# Outputs (repo root, assets/brand): logo-1024.png, logo-256.png, logo-64.png and app.ico. Copy app.ico and logo-256.png into each app project (app.ico, Assets/logo.png).
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\assets\brand\webcent-source.svg'),
    [string]$OutDir = (Join-Path $PSScriptRoot '..\..\assets\brand')
)
Add-Type -AssemblyName PresentationCore, WindowsBase
$ErrorActionPreference = 'Stop'

$svg = [IO.File]::ReadAllText((Resolve-Path $Source))
$paths = [regex]::Matches($svg, '<path fill="(#[0-9a-fA-F]{6})" d="([^"]+)"')
$arrow = [Windows.Media.Imaging.BitmapImage]::new([Uri](Resolve-Path (Join-Path $OutDir 'arrow.png')).Path)

function Draw-Logo([int]$size) {
    # The artwork lives in a 1500 x 1500 canvas; crop to the documents plus a little air.
    $crop = 110.0; $span = 1280.0
    $scale = $size / $span
    $dv = [Windows.Media.DrawingVisual]::new()
    $dc = $dv.RenderOpen()
    $dc.PushTransform([Windows.Media.ScaleTransform]::new($scale, $scale))
    $dc.PushTransform([Windows.Media.TranslateTransform]::new(-$crop, -$crop))
    $i = 0
    foreach ($m in $paths) {
        $brush = [Windows.Media.SolidColorBrush][Windows.Media.ColorConverter]::ConvertFromString($m.Groups[1].Value)
        $geo = [Windows.Media.Geometry]::Parse('F1 ' + $m.Groups[2].Value)
        $dc.DrawGeometry($brush, $null, $geo)
        $i++
        if ($i -eq 5) { $dc.DrawImage($arrow, [Windows.Rect]::new(546.18, 711.96, 1547 * 0.224262, 1164 * 0.224262)) }
    }
    $dc.Pop(); $dc.Pop(); $dc.Close()
    $rtb = [Windows.Media.Imaging.RenderTargetBitmap]::new($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    [Windows.Media.RenderOptions]::SetBitmapScalingMode($dv, [Windows.Media.BitmapScalingMode]::HighQuality)
    $rtb.Render($dv)
    return $rtb
}

function To-Png($bitmap) {
    $enc = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = [IO.MemoryStream]::new(); $enc.Save($ms); return ,$ms.ToArray()
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo-1024.png'), (To-Png (Draw-Logo 1024)))
[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo-256.png'), (To-Png (Draw-Logo 256)))
[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo-64.png'), (To-Png (Draw-Logo 64)))

# Multi-size ICO with PNG-compressed images (Windows Vista and newer).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($s in $sizes) { ,(To-Png (Draw-Logo $s)) }
$ms = [IO.MemoryStream]::new(); $bw = [IO.BinaryWriter]::new($ms)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($k = 0; $k -lt $sizes.Count; $k++) {
    $s = $sizes[$k]; $len = $images[$k].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $ms.ToArray())
Write-Host 'Wrote logo PNGs and app.ico to' (Resolve-Path $OutDir)
