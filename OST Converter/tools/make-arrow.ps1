# One-off helper: builds assets/brand/arrow.png (colour image plus alpha) from the two images embedded in the source SVG.
# The SVG uses one image as colour and the other as a luminance mask; WPF cannot do that directly, so combine them here.
Add-Type -AssemblyName PresentationCore, WindowsBase
$brand = Join-Path $PSScriptRoot '..\..\assets\brand'
$svg = [IO.File]::ReadAllText((Join-Path $brand 'webcent-source.svg'))
$imgs = [regex]::Matches($svg, 'base64,([A-Za-z0-9+/=]+)') | ForEach-Object { ,[Convert]::FromBase64String($_.Groups[1].Value) }
function Load($bytes) { $ms = [IO.MemoryStream]::new($bytes); $d = [Windows.Media.Imaging.PngBitmapDecoder]::new($ms, 'PreservePixelFormat', 'OnLoad'); [Windows.Media.Imaging.FormatConvertedBitmap]::new($d.Frames[0], [Windows.Media.PixelFormats]::Bgra32, $null, 0) }
$mask = Load $imgs[0]; $color = Load $imgs[1]
# The mask is 1547 x 1164 like the colour image (it is drawn at the same scale in the SVG).
$w = $color.PixelWidth; $h = $color.PixelHeight
$cb = [byte[]]::new($w * $h * 4); $color.CopyPixels($cb, $w * 4, 0)
$mb = [byte[]]::new($mask.PixelWidth * $mask.PixelHeight * 4); $mask.CopyPixels($mb, $mask.PixelWidth * 4, 0)
if ($mask.PixelWidth -ne $w -or $mask.PixelHeight -ne $h) { throw "mask size $($mask.PixelWidth)x$($mask.PixelHeight) differs from $w x $h" }
for ($i = 0; $i -lt $w * $h; $i++) {
    $o = $i * 4
    # luminance of the mask (Rec. 709) times its own alpha
    $lum = (0.2126 * $mb[$o + 2] + 0.7152 * $mb[$o + 1] + 0.0722 * $mb[$o]) * ($mb[$o + 3] / 255.0)
    $cb[$o + 3] = [byte][Math]::Round($lum)
}
$bmp = [Windows.Media.Imaging.BitmapSource]::Create($w, $h, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $cb, $w * 4)
$bmp = [Windows.Media.Imaging.FormatConvertedBitmap]::new($bmp, [Windows.Media.PixelFormats]::Pbgra32, $null, 0)
$enc = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
$fs = [IO.File]::Create((Join-Path $brand 'arrow.png')); $enc.Save($fs); $fs.Close()
Write-Host "arrow.png $w x $h"
