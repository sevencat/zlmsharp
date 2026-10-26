<#
.SYNOPSIS
    生成 zlmsharp 的应用图标（纯 System.Drawing 绘制，不依赖外部素材）。

.DESCRIPTION
    输出两个文件到 assets/：
      - app-icon.png  512x512，用于 README 展示
      - app.ico       16/24/32/48/64/128/256 多尺寸，供 zlmsharp.exe 的 Win32 资源使用
    修改配色或图形后重新执行本脚本，再 `dotnet build` 即可让 exe 换上图标。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools/make-icon.ps1
#>
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$base = 1024

function New-RoundedPath {
    param([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$Radius)

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $diameter = $Radius * 2
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

$bitmap = New-Object System.Drawing.Bitmap($base, $base, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$graphics.Clear([System.Drawing.Color]::Transparent)

# 圆角方形底：深海军蓝斜向渐变
$backgroundRect = New-Object System.Drawing.RectangleF(0, 0, $base, $base)
$backgroundPath = New-RoundedPath -X 0 -Y 0 -Width $base -Height $base -Radius ($base * 0.225)
$backgroundBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    $backgroundRect,
    [System.Drawing.Color]::FromArgb(255, 11, 23, 38),
    [System.Drawing.Color]::FromArgb(255, 23, 58, 88),
    45.0)
$graphics.FillPath($backgroundBrush, $backgroundPath)

# 播放三角（白）
$playPoints = @(
    (New-Object System.Drawing.PointF(($base * 0.235), ($base * 0.29))),
    (New-Object System.Drawing.PointF(($base * 0.235), ($base * 0.71))),
    (New-Object System.Drawing.PointF(($base * 0.575), ($base * 0.50)))
)
$playPath = New-Object System.Drawing.Drawing2D.GraphicsPath
$playPath.AddPolygon($playPoints)
$playBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 248, 250, 252))
$graphics.FillPath($playBrush, $playPath)

# 三条青色信号条（等宽、胶囊形、底部对齐）
$barBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 34, 211, 238))
$barWidth = $base * 0.072
$barBottom = $base * 0.715
@(
    @{ X = 0.630; Height = 0.185 },
    @{ X = 0.727; Height = 0.300 },
    @{ X = 0.824; Height = 0.425 }
) | ForEach-Object {
    $height = $base * $_.Height
    $path = New-RoundedPath -X ($base * $_.X) -Y ($barBottom - $height) `
        -Width $barWidth -Height $height -Radius ($barWidth / 2)
    $graphics.FillPath($barBrush, $path)
    $path.Dispose()
}

$graphics.Dispose()
$backgroundBrush.Dispose()
$backgroundPath.Dispose()
$barBrush.Dispose()
$playBrush.Dispose()
$playPath.Dispose()

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDir)
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

# 预览图（README 用）
$preview = New-Object System.Drawing.Bitmap(512, 512)
$previewGraphics = [System.Drawing.Graphics]::FromImage($preview)
$previewGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$previewGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$previewGraphics.DrawImage($bitmap, 0, 0, 512, 512)
$previewGraphics.Dispose()
$previewPath = Join-Path $resolvedOutput 'app-icon.png'
$preview.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

# 多尺寸 ICO（PNG 压缩帧，Vista 及以上原生支持）
$frames = @()
foreach ($size in $sizes) {
    $frame = New-Object System.Drawing.Bitmap($size, $size)
    $frameGraphics = [System.Drawing.Graphics]::FromImage($frame)
    $frameGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $frameGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $frameGraphics.DrawImage($bitmap, 0, 0, $size, $size)
    $frameGraphics.Dispose()

    $stream = New-Object System.IO.MemoryStream
    $frame.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += , @($size, $stream.ToArray())
    $stream.Dispose()
    $frame.Dispose()
}

$bitmap.Dispose()

$iconPath = Join-Path $resolvedOutput 'app.ico'
$fileStream = [System.IO.File]::Create($iconPath)
$writer = New-Object System.IO.BinaryWriter($fileStream)

$writer.Write([UInt16]0)                # reserved
$writer.Write([UInt16]1)                # type = icon
$writer.Write([UInt16]$frames.Count)    # image count

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    $size = [int]$frame[0]
    $data = [byte[]]$frame[1]
    $dimension = if ($size -ge 256) { 0 } else { $size }

    $writer.Write([byte]$dimension)     # width
    $writer.Write([byte]$dimension)     # height
    $writer.Write([byte]0)              # palette count
    $writer.Write([byte]0)              # reserved
    $writer.Write([UInt16]1)            # color planes
    $writer.Write([UInt16]32)           # bits per pixel
    $writer.Write([UInt32]$data.Length) # bytes in resource
    $writer.Write([UInt32]$offset)      # offset
    $offset += $data.Length
}

foreach ($frame in $frames) {
    $writer.Write([byte[]]$frame[1])
}

$writer.Flush()
$writer.Dispose()
$fileStream.Dispose()

Write-Host "已生成：$iconPath"
Write-Host "已生成：$previewPath"
