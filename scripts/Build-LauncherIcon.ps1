param([string]$Source = (Join-Path $PSScriptRoot '..\src\MechanicaLauncher\Assets\Mechanica.png'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\src\MechanicaLauncher\Assets')).Path
$sourceImage = [Drawing.Image]::FromFile((Resolve-Path -LiteralPath $Source).Path)
try {
    if ($sourceImage.Width -ne $sourceImage.Height) { throw 'Launcher icon source must be square' }
    $frames = foreach ($size in @(16,20,24,32,40,48,64,128,256)) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $stream = [IO.MemoryStream]::new()
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($sourceImage, [Drawing.Rectangle]::new(0,0,$size,$size), 0,0,$sourceImage.Width,$sourceImage.Height,[Drawing.GraphicsUnit]::Pixel,$attributes)
            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
            @{ Size = $size; Bytes = $stream.ToArray() }
        } finally { $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $stream.Dispose() }
    }
} finally { $sourceImage.Dispose() }
$iconPath = Join-Path $assetDirectory 'Mechanica.ico'
$output = [IO.File]::Create($iconPath)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output ('Created ' + $iconPath + ' (16, 20, 24, 32, 40, 48, 64, 128, 256 px)')
