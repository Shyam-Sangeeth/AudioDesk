# Renders the AudioDesk logo (logo.svg's design) to PNGs and a multi-size AudioDesk.ico.
# Run from Windows PowerShell:  powershell -ExecutionPolicy Bypass -File Assets\make-icons.ps1
param([string]$OutDir = $PSScriptRoot)

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

function New-Logo([int]$size) {
    # Designed on a 256 x 256 grid.
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object System.Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))

    # Rounded square with a blue-to-violet gradient.
    $gradient = New-Object System.Windows.Media.LinearGradientBrush
    $gradient.StartPoint = '0,0'; $gradient.EndPoint = '1,1'
    $gradient.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x2F, 0x6B, 0xFF)), 0))
    $gradient.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x8A, 0x3F, 0xFC)), 1))
    $dc.DrawRoundedRectangle($gradient, $null, (New-Object System.Windows.Rect 8, 8, 240, 240), 56, 56)

    # Soft top highlight for depth.
    $shine = New-Object System.Windows.Media.LinearGradientBrush
    $shine.StartPoint = '0,0'; $shine.EndPoint = '0,1'
    $shine.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(0x38, 0xFF, 0xFF, 0xFF)), 0))
    $shine.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromArgb(0x00, 0xFF, 0xFF, 0xFF)), 0.55))
    $dc.DrawRoundedRectangle($shine, $null, (New-Object System.Windows.Rect 8, 8, 240, 240), 56, 56)

    # Three faders: faint tracks with white knobs at different levels.
    $track = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0x66, 0xFF, 0xFF, 0xFF))
    $knob = [System.Windows.Media.Brushes]::White
    $faders = @(@{ X = 80; Y = 156 }, @{ X = 128; Y = 96 }, @{ X = 176; Y = 132 })
    foreach ($f in $faders) {
        $dc.DrawRoundedRectangle($track, $null, (New-Object System.Windows.Rect ($f.X - 8), 58, 16, 140), 8, 8)
        $dc.DrawRoundedRectangle($knob, $null, (New-Object System.Windows.Rect ($f.X - 24), ($f.Y - 14), 48, 28), 14, 14)
    }

    $dc.Pop()
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

# At 16 px the scaled design blurs, so the tray size gets a hand-placed version on whole pixels:
# 2-px tracks and 4x2 knobs, with stronger tracks so they still read.
function New-Logo16 {
    $visual = New-Object System.Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()

    $gradient = New-Object System.Windows.Media.LinearGradientBrush
    $gradient.StartPoint = '0,0'; $gradient.EndPoint = '1,1'
    $gradient.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x2F, 0x6B, 0xFF)), 0))
    $gradient.GradientStops.Add((New-Object System.Windows.Media.GradientStop ([System.Windows.Media.Color]::FromRgb(0x8A, 0x3F, 0xFC)), 1))
    $dc.DrawRoundedRectangle($gradient, $null, (New-Object System.Windows.Rect 0, 0, 16, 16), 3.5, 3.5)

    $track = New-Object System.Windows.Media.SolidColorBrush ([System.Windows.Media.Color]::FromArgb(0x80, 0xFF, 0xFF, 0xFF))
    $knob = [System.Windows.Media.Brushes]::White
    $faders = @(@{ X = 3; Y = 9 }, @{ X = 7; Y = 4 }, @{ X = 11; Y = 7 })
    foreach ($f in $faders) {
        $dc.DrawRectangle($track, $null, (New-Object System.Windows.Rect $f.X, 3, 2, 10))
        $dc.DrawRectangle($knob, $null, (New-Object System.Windows.Rect ($f.X - 1), $f.Y, 4, 2))
    }
    $dc.Close()

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap 16, 16, 96, 96, ([System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.MemoryStream
    $encoder.Save($stream)
    return ,$stream.ToArray()
}

# Icon sizes Windows uses for the tray, title bar, taskbar, Explorer and Start (incl. high-DPI variants).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($s in $sizes) { $images[$s] = if ($s -eq 16) { New-Logo16 } else { New-Logo $s } }

[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo-256.png'), $images[256])
[IO.File]::WriteAllBytes((Join-Path $OutDir 'logo-64.png'), $images[64])

# ICO container with PNG-compressed images (supported by Windows Vista and later).
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $dim = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$s].Length); $w.Write([uint32]$offset)
    $offset += $images[$s].Length
}
foreach ($s in $sizes) { $w.Write($images[$s]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'AudioDesk.ico'), $ico.ToArray())

"Wrote AudioDesk.ico ($($sizes -join ', ') px), logo-256.png and logo-64.png to $OutDir"
