param(
    [string]$OutputPath = (Join-Path $PSScriptRoot "..\src\VisionStudio.App\Assets\VisionStudio.ico")
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = [System.Collections.Generic.List[byte[]]]::new()

foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.Clear([System.Drawing.Color]::Transparent)

        $inset = [Math]::Max(1.0, $size * 0.045)
        $side = $size - (2 * $inset)
        $radius = $size * 0.19
        $diameter = $radius * 2
        $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
        try {
            $path.AddArc($inset, $inset, $diameter, $diameter, 180, 90)
            $path.AddArc($inset + $side - $diameter, $inset, $diameter, $diameter, 270, 90)
            $path.AddArc($inset + $side - $diameter, $inset + $side - $diameter, $diameter, $diameter, 0, 90)
            $path.AddArc($inset, $inset + $side - $diameter, $diameter, $diameter, 90, 90)
            $path.CloseFigure()

            $blue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 0, 122, 204))
            try { $graphics.FillPath($blue, $path) } finally { $blue.Dispose() }
        }
        finally {
            $path.Dispose()
        }

        $penWidth = [Math]::Max(1.6, $size * 0.105)
        $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $penWidth)
        try {
            $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
            $graphics.DrawLines($pen, [System.Drawing.PointF[]]@(
                [System.Drawing.PointF]::new($size * 0.27, $size * 0.30),
                [System.Drawing.PointF]::new($size * 0.50, $size * 0.73),
                [System.Drawing.PointF]::new($size * 0.73, $size * 0.30)
            ))
        }
        finally {
            $pen.Dispose()
        }

        $stream = [System.IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $images.Add($stream.ToArray())
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

$fullOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fullOutputPath)) | Out-Null
$file = [System.IO.File]::Create($fullOutputPath)
$writer = [System.IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)

    $offset = 6 + (16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $size = $sizes[$index]
        $image = $images[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$image.Length)
        $writer.Write([uint32]$offset)
        $offset += $image.Length
    }

    foreach ($image in $images) {
        $writer.Write($image)
    }
}
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output $fullOutputPath
