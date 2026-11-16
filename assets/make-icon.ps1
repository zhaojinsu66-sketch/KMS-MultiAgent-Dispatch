# Draws the KMS app icon (rounded dark tile, white "KMS", coral underline) and writes
# assets\kms.ico (16..256 px) plus assets\kms.png (256 px, used for the window icon).
# Usage: powershell -ExecutionPolicy Bypass -File assets\make-icon.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$dir = Split-Path -Parent $MyInvocation.MyCommand.Path

function New-KmsBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'; $g.InterpolationMode = 'HighQualityBicubic'
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded tile
    $r = [float]($size * 0.22); $d = $r * 2; $m = [float]($size * 0.03); $w = $size - 2 * $m
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($m, $m, $d, $d, 180, 90); $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
    $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $rect = New-Object System.Drawing.RectangleF($m, $m, $w, $w)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 44, 44, 46), [System.Drawing.Color]::FromArgb(255, 20, 20, 21), 90)
    $g.FillPath($bg, $path)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $coral = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 217, 119, 87))
    $fmt = New-Object System.Drawing.StringFormat([System.Drawing.StringFormat]::GenericTypographic)
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'

    # Small sizes cannot hold three letters legibly: draw a bold K there, the full KMS from 32 px up.
    $text = if ($size -lt 32) { 'K' } else { 'KMS' }
    $family = 'Segoe UI'
    $px = if ($text -eq 'K') { $size * 0.66 } else { $size * 0.36 }
    $font = New-Object System.Drawing.Font($family, [float]$px, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    # shrink until it fits the tile width with a margin
    while ($g.MeasureString($text, $font, 9999, $fmt).Width -gt $w * 0.80 -and $px -gt 4) {
        $px *= 0.96; $font.Dispose()
        $font = New-Object System.Drawing.Font($family, [float]$px, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    }
    $cy = if ($text -eq 'K') { $size * 0.50 } else { $size * 0.47 }
    $g.DrawString($text, $font, $white, [float]($size / 2), [float]$cy, $fmt)

    # coral underline
    $tw = $g.MeasureString($text, $font, 9999, $fmt).Width
    $barH = [Math]::Max(1.5, $size * 0.055); $barW = if ($text -eq 'K') { $size * 0.34 } else { $tw * 0.96 }
    $barY = if ($text -eq 'K') { $size * 0.80 } else { $size * 0.70 }
    $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
    $bx = ($size - $barW) / 2; $br = $barH / 2
    $bar.AddArc([float]$bx, [float]$barY, [float]$barH, [float]$barH, 90, 180); $bar.AddArc([float]($bx + $barW - $barH), [float]$barY, [float]$barH, [float]$barH, 270, 180); $bar.CloseFigure()
    $g.FillPath($coral, $bar)
    $g.Dispose()
    return $bmp
}

# Classic uncompressed icon image (works everywhere): BITMAPINFOHEADER + bottom-up BGRA rows + empty AND mask
function Get-DibBytes($bmp) {
    $s = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter($ms)
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A) }
    }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $w.Write([byte[]](New-Object byte[] ($maskRow * $s)))
    $w.Flush()
    return , ([byte[]]$ms.ToArray())   # the comma keeps it one byte[] instead of unrolling it
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = @()
$sheet = New-Object System.Drawing.Bitmap(640, 290)
$sg = [System.Drawing.Graphics]::FromImage($sheet); $sg.Clear([System.Drawing.Color]::FromArgb(255, 243, 243, 240)); $px = 300
foreach ($s in $sizes) {
    $bmp = New-KmsBitmap $s
    if ($s -eq 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngs += , $ms.ToArray()
        $bmp.Save((Join-Path $dir 'kms.png'), [System.Drawing.Imaging.ImageFormat]::Png)
        $sg.DrawImage($bmp, 16, 16, 256, 256)
    } else {
        $pngs += , ([byte[]](Get-DibBytes $bmp))
        if ($s -in 128, 64, 48, 32, 24, 16) { $sg.DrawImage($bmp, $px, 16, $s, $s); $px += $s + 10 }
    }
    $bmp.Dispose()
}
$sg.Dispose(); $sheet.Save((Join-Path $dir 'kms-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png); $sheet.Dispose()

# ICO container: classic images for the small sizes, PNG for 256
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($out)
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $b = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$b); $bw.Write([byte]$b); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write([byte[]]$p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $dir 'kms.ico'), $out.ToArray())
Write-Output ("Wrote kms.ico (" + $out.Length + " bytes) and kms.png")
