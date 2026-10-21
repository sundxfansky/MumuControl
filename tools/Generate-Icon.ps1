# Regenerates the Windows multi-resolution icon from the same geometry as favicon.svg.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$projectRoot = Split-Path $PSScriptRoot -Parent
$assetDirectory = Join-Path $projectRoot 'src\MumuControl\assets'
[IO.Directory]::CreateDirectory($assetDirectory) | Out-Null
$iconImages = [Collections.Generic.List[byte[]]]::new()
$iconSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
foreach ($iconSize in $iconSizes) {
    $scale = 4
    $bitmap = [Drawing.Bitmap]::new($iconSize * $scale, $iconSize * $scale)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = 'AntiAlias'
    $graphics.ScaleTransform(($iconSize * $scale / 64.0), ($iconSize * $scale / 64.0))
    $outline = [Drawing.Drawing2D.GraphicsPath]::new()
    $outline.AddArc(1, 1, 34, 34, 180, 90)
    $outline.AddArc(29, 1, 34, 34, 270, 90)
    $outline.AddArc(29, 29, 34, 34, 0, 90)
    $outline.AddArc(1, 29, 34, 34, 90, 90)
    $outline.CloseFigure()
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(1, 1), [Drawing.Point]::new(63, 63), [Drawing.ColorTranslator]::FromHtml('#917af2'), [Drawing.ColorTranslator]::FromHtml('#5442b9'))
    $graphics.FillPath($gradient, $outline)
    $border = [Drawing.Pen]::new([Drawing.Color]::FromArgb(46, 255, 255, 255), 0.7)
    $graphics.DrawPath($border, $outline)
    $mPen = [Drawing.Pen]::new([Drawing.Color]::White, 6.5)
    $mPen.StartCap = 'Round'; $mPen.EndCap = 'Round'; $mPen.LineJoin = 'Round'
    $mPoints = [Drawing.PointF[]]@([Drawing.PointF]::new(16,45), [Drawing.PointF]::new(16,21), [Drawing.PointF]::new(32,36), [Drawing.PointF]::new(48,21), [Drawing.PointF]::new(48,45))
    $graphics.DrawLines($mPen, $mPoints)
    $mint = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#d0f7c1'))
    $graphics.FillEllipse($mint, 46.7, 9.7, 6.6, 6.6)
    $output = [Drawing.Bitmap]::new($iconSize, $iconSize)
    $resize = [Drawing.Graphics]::FromImage($output)
    $resize.InterpolationMode = 'HighQualityBicubic'
    $resize.DrawImage($bitmap, 0, 0, $iconSize, $iconSize)
    $stream = [IO.MemoryStream]::new()
    $output.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
    $iconImages.Add($stream.ToArray())
    if ($iconSize -eq 256) {
        $artifactDirectory = Join-Path $projectRoot 'artifacts'
        [IO.Directory]::CreateDirectory($artifactDirectory) | Out-Null
        $output.Save((Join-Path $artifactDirectory 'icon-preview.png'), [Drawing.Imaging.ImageFormat]::Png)
    }
    $stream.Dispose(); $resize.Dispose(); $output.Dispose(); $mint.Dispose(); $mPen.Dispose(); $border.Dispose(); $gradient.Dispose(); $outline.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$iconStream = [IO.File]::Create((Join-Path $assetDirectory 'MumuControl.ico'))
$writer = [IO.BinaryWriter]::new($iconStream)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$iconSizes.Count)
    $offset = 6 + 16 * $iconSizes.Count
    for ($i = 0; $i -lt $iconSizes.Count; $i++) {
        $dimension = if ($iconSizes[$i] -eq 256) { 0 } else { $iconSizes[$i] }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$iconImages[$i].Length); $writer.Write([uint32]$offset)
        $offset += $iconImages[$i].Length
    }
    foreach ($iconImage in $iconImages) { $writer.Write($iconImage) }
} finally { $writer.Dispose(); $iconStream.Dispose() }
