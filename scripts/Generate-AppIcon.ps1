# Exports the checked-in transparent master; ordinary builds use the checked-in PNG/ICO.
param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Artwork/tractortool-master.png'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'TractorTool/Assets')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$sourceImage = [Drawing.Bitmap]::new([IO.Path]::GetFullPath($Source))
try {
    if ($sourceImage.Width -ne $sourceImage.Height) { throw '图标母版必须为正方形透明 PNG。' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $sizes = @(16, 20, 24, 32, 48, 64, 128, 256)
    $images = [Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $attributes = [Drawing.Imaging.ImageAttributes]::new()
        $memory = [IO.MemoryStream]::new()
        try {
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $attributes.SetWrapMode([Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $graphics.DrawImage($sourceImage, [Drawing.Rectangle]::new(0, 0, $size, $size),
                0, 0, $sourceImage.Width, $sourceImage.Height, [Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($memory, [Drawing.Imaging.ImageFormat]::Png)
            $images.Add($memory.ToArray())
        }
        finally { $memory.Dispose(); $attributes.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'tractortool.png'), $images[$images.Count - 1])
    $stream = [IO.File]::Create((Join-Path $OutputDirectory 'tractortool.ico'))
    $writer = [IO.BinaryWriter]::new($stream)
    try {
        # ICONDIR and one ICONDIRENTRY per resolution, followed by PNG payloads with alpha.
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$images[$i].Length); $writer.Write([uint32]$offset)
            $offset += $images[$i].Length
        }
        foreach ($bytes in $images) { $writer.Write($bytes) }
    }
    finally { $writer.Dispose() }
    Write-Output ('Exported transparent PNG and ICO: ' + ($sizes -join ', ') + ' px')
}
finally { $sourceImage.Dispose() }
