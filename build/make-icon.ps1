# Writes assets\Sikirincot.ico: the same blue tile, crop corners and redaction bar that AppIcon.cs
# draws at runtime, at 16-256 px, as a PNG-compressed multi-size .ico for the exe and the installer.
# Run from anywhere: powershell -File build\make-icon.ps1

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'assets\Sikirincot.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0
    $tile = New-RoundRect (1 * $s) (1 * $s) (30 * $s) (30 * $s) (7 * $s)
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0, 120, 212))), $tile)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), (3 * $s)
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF (8 * $s), (14 * $s)), (New-Object System.Drawing.PointF (8 * $s), (8 * $s)), (New-Object System.Drawing.PointF (14 * $s), (8 * $s))))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF (18 * $s), (24 * $s)), (New-Object System.Drawing.PointF (24 * $s), (24 * $s)), (New-Object System.Drawing.PointF (24 * $s), (18 * $s))))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(235, 255, 255, 255))), (11 * $s), (14.5 * $s), (10 * $s), (3 * $s))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { , (New-IconPng $size) }

# ICO layout: 6-byte header, 16-byte directory entry per image, then the PNG data.
$file = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $file
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes($out, $file.ToArray())
"Wrote $out ($($file.Length) bytes, sizes $($sizes -join ', '))"
