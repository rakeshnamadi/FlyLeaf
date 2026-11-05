# Draws the Flyleaf icon (a page with a folded corner and note lines) and writes a multi-size .ico.
# Also writes the PNG logos the Store package needs.
# The results are committed under src\, so this only needs to run if the design changes.
param(
    [string]$OutFile = (Join-Path $PSScriptRoot '..\src\Flyleaf\Flyleaf.ico'),
    [string]$AssetsDir = (Join-Path $PSScriptRoot '..\src\FlyleafStore\Package\Assets')
)

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $size / 32.0
    $fold = 9 * $s
    $left = 5 * $s; $top = 2 * $s; $right = 27 * $s; $bottom = 30 * $s

    $page = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $left, $top),
        (New-Object System.Drawing.PointF ($right - $fold), $top),
        (New-Object System.Drawing.PointF $right, ($top + $fold)),
        (New-Object System.Drawing.PointF $right, $bottom),
        (New-Object System.Drawing.PointF $left, $bottom))
    $paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 15, 118, 110))
    $g.FillPolygon($paper, $page)

    $flap = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($right - $fold), $top),
        (New-Object System.Drawing.PointF $right, ($top + $fold)),
        (New-Object System.Drawing.PointF ($right - $fold), ($top + $fold)))
    $flapBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 94, 234, 212))
    $g.FillPolygon($flapBrush, $flap)

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $lineHeight = [Math]::Max(1.0, 2.4 * $s)
    foreach ($line in @(@(14, 13), @(19, 16), @(24, 10))) {
        $g.FillRectangle($ink, 9 * $s, $line[0] * $s, $line[1] * $s, $lineHeight)
    }

    $g.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$stream.ToArray()
}

$sizes = 16, 20, 24, 32, 48, 64, 256
$images = $sizes | ForEach-Object { , (New-IconPng $_) }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $out
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($image in $images) { $writer.Write([byte[]]$image) }
$writer.Flush()

[System.IO.File]::WriteAllBytes([System.IO.Path]::GetFullPath($OutFile), $out.ToArray())

New-Item -ItemType Directory $AssetsDir -Force | Out-Null
$logos = @{ 'StoreLogo.png' = 50; 'Square44x44Logo.png' = 44; 'Square150x150Logo.png' = 150 }
foreach ($logo in $logos.GetEnumerator()) {
    $path = Join-Path ([System.IO.Path]::GetFullPath($AssetsDir)) $logo.Key
    [System.IO.File]::WriteAllBytes($path, (New-IconPng $logo.Value))
}
