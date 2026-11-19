# Draws the app icon, src/GameBackdrop/GameBackdrop.ico: a game window centred on a black screen,
# which is what the app does. Each size is drawn on its own pixel grid (at 8x, then scaled down),
# so the 16 px tray icon stays sharp. Every entry is stored as PNG, which Windows reads at any size.
# Run it from the repo root in Windows PowerShell: .\tools\make-icon.ps1 [-Preview <png>]
param([string] $Preview)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# 96 and 128 too: System.Drawing.Icon never picks the 256 entry (stored as 0), so at 250%+
# scaling the large icon size (80, 96) would otherwise stretch the 64.
$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$out = Join-Path $PSScriptRoot '..\src\GameBackdrop\GameBackdrop.ico'

function New-RoundedRect([float] $x, [float] $y, [float] $w, [float] $h, [float] $r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $path
}

function Draw-Icon([int] $size) {
    $k = 8
    $big = New-Object System.Drawing.Bitmap ($size * $k), ($size * $k)
    $g = [System.Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # The screen: near-black with a light edge, so it shows on a dark taskbar too.
    $edge = [Math]::Max(1, [Math]::Round($size / 32))
    $radius = [Math]::Max(2, [Math]::Round($size * 0.16))
    $screen = New-RoundedRect 0 0 ($size * $k) ($size * $k) ($radius * $k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 118, 118, 118))), $screen)
    $inner = New-RoundedRect ($edge * $k) ($edge * $k) (($size - 2 * $edge) * $k) (($size - 2 * $edge) * $k) ([Math]::Max(1, $radius - $edge) * $k)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 10, 10))), $inner)

    # The game: a 4:3 window in the middle, on whole pixels with an even margin.
    $w = [int]([Math]::Round($size * 0.6 / 2) * 2)
    $h = [int]([Math]::Round($w * 0.75 / 2) * 2)
    if ($size % 2 -eq 1) { $w--; $h-- }
    $x = ($size - $w) / 2
    $y = ($size - $h) / 2
    $gameRadius = [Math]::Round($size * 0.04)
    $amber = [System.Drawing.Color]::FromArgb(255, 245, 176, 0)
    if ($gameRadius -ge 1) {
        $game = New-RoundedRect ($x * $k) ($y * $k) ($w * $k) ($h * $k) ($gameRadius * $k)
        $g.FillPath((New-Object System.Drawing.SolidBrush $amber), $game)
    } else {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush $amber), ($x * $k), ($y * $k), ($w * $k), ($h * $k))
    }
    $g.Dispose()

    $small = New-Object System.Drawing.Bitmap $size, $size
    $s = [System.Drawing.Graphics]::FromImage($small)
    $s.InterpolationMode = 'HighQualityBicubic'
    $s.PixelOffsetMode = 'HighQuality'
    $s.DrawImage($big, 0, 0, $size, $size)
    $s.Dispose()
    $big.Dispose()
    $small
}

$images = foreach ($size in $sizes) {
    $bitmap = Draw-Icon $size
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    , @($size, $stream.ToArray())
}

# ICO: a 6-byte header, a 16-byte entry per image, then the PNG data.
$file = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([UInt16]0); $writer.Write([UInt16]1); $writer.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($image in $images) {
    $size, $png = $image
    $dim = if ($size -ge 256) { 0 } else { $size }
    $writer.Write([byte]$dim); $writer.Write([byte]$dim); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([UInt16]1); $writer.Write([UInt16]32)
    $writer.Write([UInt32]$png.Length); $writer.Write([UInt32]$offset)
    $offset += $png.Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image[1]) }
$writer.Flush()
[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($out), $file.ToArray())
Write-Host "Wrote $out"

# A sheet of every size on light and dark grounds, at 4x, for a look.
if ($Preview) {
    # .NET resolves a relative path against the process's folder, not PowerShell's location.
    $Preview = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Preview)
    $pad = 16
    $cell = 256 + $pad
    $sheet = New-Object System.Drawing.Bitmap ($cell * $sizes.Count + $pad), (2 * $cell + $pad)
    $g = [System.Drawing.Graphics]::FromImage($sheet)
    $g.InterpolationMode = 'NearestNeighbor'
    $g.PixelOffsetMode = 'Half'
    $g.FillRectangle([System.Drawing.Brushes]::WhiteSmoke, 0, 0, $sheet.Width, $cell)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 32, 32, 32))), 0, $cell, $sheet.Width, $cell + $pad)
    $i = 0
    foreach ($image in $images) {
        $size, $png = $image
        $bitmap = [System.Drawing.Image]::FromStream((New-Object System.IO.MemoryStream (, $png)))
        $scale = [Math]::Min(4, [Math]::Floor(256 / $size))
        foreach ($row in 0, 1) {
            $g.DrawImage($bitmap, $pad + $i * $cell, $pad + $row * $cell, $size * $scale, $size * $scale)
        }
        $i++
    }
    $g.Dispose()
    $sheet.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host "Wrote $Preview"
}
