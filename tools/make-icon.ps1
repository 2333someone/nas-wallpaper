param(
    [string]$Output = (Join-Path $PSScriptRoot '..\src\NasWallpaper\Assets\app.ico')
)

Add-Type -AssemblyName System.Drawing

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.InterpolationMode = 'HighQualityBicubic'
    $g.PixelOffsetMode = 'HighQuality'

    $s = $size / 256.0
    $radius = 58 * $s
    $d = $radius * 2

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($size - $d, 0, $d, $d, 270, 90)
    $path.AddArc($size - $d, $size - $d, $d, $d, 0, 90)
    $path.AddArc(0, $size - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        $rect,
        [System.Drawing.Color]::FromArgb(255, 168, 152, 255),
        [System.Drawing.Color]::FromArgb(255, 88, 70, 232),
        45.0)
    $g.FillPath($brush, $path)
    $g.SetClip($path)

    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(242, 255, 255, 255))
    $sun = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(235, 255, 255, 255))
    $g.FillEllipse($sun, 152 * $s, 74 * $s, 36 * $s, 36 * $s)

    $mountain = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF -ArgumentList ([float](44 * $s)), ([float](186 * $s))),
        (New-Object System.Drawing.PointF -ArgumentList ([float](104 * $s)), ([float](108 * $s))),
        (New-Object System.Drawing.PointF -ArgumentList ([float](140 * $s)), ([float](156 * $s))),
        (New-Object System.Drawing.PointF -ArgumentList ([float](166 * $s)), ([float](128 * $s))),
        (New-Object System.Drawing.PointF -ArgumentList ([float](212 * $s)), ([float](186 * $s)))
    )
    $mountain.AddPolygon($pts)
    $g.FillPath($white, $mountain)

    $g.ResetClip()
    $sun.Dispose(); $white.Dispose(); $mountain.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
    return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$images = @()
foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $images += , @{ Size = $size; Bytes = $ms.ToArray() }
    $ms.Dispose(); $bmp.Dispose()
}

$dir = Split-Path -Parent $Output
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }

$stream = [System.IO.File]::Create($Output)
$writer = New-Object System.IO.BinaryWriter($stream)
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$images.Count)

$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $writer.Write([byte]$dim)
    $writer.Write([byte]$dim)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$img.Bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $writer.Write($img.Bytes) }
$writer.Flush(); $writer.Dispose(); $stream.Dispose()

$preview = Join-Path $dir 'app-icon-preview.png'
$bmp = New-IconBitmap 256
$bmp.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

"icon: $Output"
"preview: $preview"
