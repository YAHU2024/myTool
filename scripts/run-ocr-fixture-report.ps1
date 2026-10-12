[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixtureDirectory,

    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,

    [string]$OutputPath = "ocr-fixture-report.json",
    [string]$LanguageHint,
    [switch]$AllowLanguageFallback,

    # 可选的本地人工验收输出目录。启用后会写入带框标注图和 OCR 文本侧车文件；
    # 默认不输出识别文本，避免把隐私内容带入脱敏 JSON 报告。
    [string]$PreviewDirectory
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $FixtureDirectory -PathType Container)) {
    throw "Fixture 目录不存在：$FixtureDirectory"
}
if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
    throw "QuickTranslate 程序集不存在：$AssemblyPath"
}

Add-Type -AssemblyName System.Drawing
Add-Type -Path (Resolve-Path -LiteralPath $AssemblyPath)

function Get-LevenshteinDistance([string]$first, [string]$second) {
    $left = if ($null -eq $first) { "" } else { $first }
    $right = if ($null -eq $second) { "" } else { $second }
    $previous = [int[]]::new($right.Length + 1)
    for ($j = 0; $j -le $right.Length; $j++) { $previous[$j] = $j }

    for ($i = 1; $i -le $left.Length; $i++) {
        $current = [int[]]::new($right.Length + 1)
        $current[0] = $i
        for ($j = 1; $j -le $right.Length; $j++) {
            $cost = if ($left[$i - 1] -eq $right[$j - 1]) { 0 } else { 1 }
            $current[$j] = [Math]::Min(
                [Math]::Min($current[$j - 1] + 1, $previous[$j] + 1),
                $previous[$j - 1] + $cost)
        }
        $previous = $current
    }
    return $previous[$right.Length]
}

function ConvertTo-OcrImage([string]$path) {
    $bitmap = [System.Drawing.Bitmap]::new($path)
    try {
        $rect = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
        $data = $bitmap.LockBits(
            $rect,
            [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
            [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
        try {
            $stride = [Math]::Abs($data.Stride)
            $payload = [byte[]]::new($stride * $bitmap.Height)
            if ($data.Stride -gt 0) {
                [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $payload, 0, $payload.Length)
            } else {
                for ($row = 0; $row -lt $bitmap.Height; $row++) {
                    $source = [IntPtr]::Add($data.Scan0, ($bitmap.Height - 1 - $row) * $data.Stride)
                    [System.Runtime.InteropServices.Marshal]::Copy($source, $payload, $row * $stride, $stride)
                }
            }
            return [QuickTranslate.Models.OcrImage]::new(
                $bitmap.Width,
                $bitmap.Height,
                $stride,
                [System.ReadOnlyMemory[byte]]::new($payload))
        }
        finally {
            $bitmap.UnlockBits($data)
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

function Write-OcrPreview(
    [string]$sourcePath,
    [QuickTranslate.Models.OcrResult]$result,
    [string]$previewDirectory) {
    if ([string]::IsNullOrWhiteSpace($previewDirectory)) {
        return
    }

    if (-not (Test-Path -LiteralPath $previewDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $previewDirectory -Force | Out-Null
    }

    $baseName = [IO.Path]::GetFileNameWithoutExtension($sourcePath)
    $imageOutputPath = Join-Path $previewDirectory ($baseName + ".ocr-preview.png")
    $textOutputPath = Join-Path $previewDirectory ($baseName + ".ocr.txt")
    # 先转为可绘制的 32bpp 图像，兼容索引色 PNG/8bpp BMP 等输入。
    $sourceBitmap = [System.Drawing.Bitmap]::new($sourcePath)
    $bitmap = [System.Drawing.Bitmap]::new(
        $sourceBitmap.Width,
        $sourceBitmap.Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.DrawImageUnscaled($sourceBitmap, 0, 0)
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
            $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::Red, 2)
            $labelBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(210, 0, 0, 0))
            $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::Yellow)
            $font = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Regular)
            try {
                $textLines = [System.Collections.Generic.List[string]]::new()
                $index = 0
                foreach ($block in $result.Blocks) {
                    $index++
                    $bounds = $block.Bounds
                    if (-not $bounds.IsWithin($bitmap.Width, $bitmap.Height)) {
                        continue
                    }

                    $rectangle = [System.Drawing.Rectangle]::new(
                        $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height)
                    $graphics.DrawRectangle($pen, $rectangle)

                    $displayText = ($block.Text -replace "\s+", " ").Trim()
                    if ($displayText.Length -gt 120) {
                        $displayText = $displayText.Substring(0, 120) + "..."
                    }
                    if ([string]::IsNullOrWhiteSpace($displayText)) {
                        $displayText = "(空文本)"
                    }
                    $labelSize = $graphics.MeasureString($displayText, $font)
                    $labelX = [Math]::Max(0, $bounds.X)
                    $labelY = [Math]::Max(0, $bounds.Y - [int]$labelSize.Height - 2)
                    $labelWidth = [Math]::Min($bitmap.Width - $labelX, [int]$labelSize.Width + 6)
                    $labelHeight = [Math]::Min($bitmap.Height - $labelY, [int]$labelSize.Height + 4)
                    if ($labelWidth -gt 0 -and $labelHeight -gt 0) {
                        $graphics.FillRectangle($labelBrush, $labelX, $labelY, $labelWidth, $labelHeight)
                        $graphics.DrawString($displayText, $font, $textBrush, $labelX + 3, $labelY + 1)
                    }
                    $textLines.Add("$($block.BlockId)`t$($bounds.X),$($bounds.Y),$($bounds.Width),$($bounds.Height)`t$($block.Text)")
                }
                $bitmap.Save($imageOutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
                $header = @(
                    "# OCR preview: $([IO.Path]::GetFileName($sourcePath))",
                    "# language: $($result.UsedLanguageTag); blocks: $($result.Blocks.Count); angle: $($result.TextAngleDegrees)",
                    "# columns: BlockId<TAB>X,Y,Width,Height<TAB>Text"
                )
                @($header + $textLines) | Set-Content -LiteralPath $textOutputPath -Encoding UTF8
            }
            finally {
                $font.Dispose()
                $textBrush.Dispose()
                $labelBrush.Dispose()
                $pen.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }
    }
    finally {
        $bitmap.Dispose()
        $sourceBitmap.Dispose()
    }
}

$service = [QuickTranslate.Services.WindowsMediaOcrService]::new()
$capability = $service.Probe()
$options = [QuickTranslate.Models.OcrRecognitionOptions]::new(
    $LanguageHint,
    [bool]$AllowLanguageFallback)
$extensions = @(".png", ".jpg", ".jpeg", ".bmp")
$files = Get-ChildItem -LiteralPath $FixtureDirectory -File |
    Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() } |
    Sort-Object Name

$rows = foreach ($file in $files) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $sha = [BitConverter]::ToString(
        [Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($file.FullName))).Replace("-", "")
    $expectedPath = [IO.Path]::ChangeExtension($file.FullName, ".txt")
    $expected = if (Test-Path -LiteralPath $expectedPath) {
        [QuickTranslate.Core.OcrTextNormalizer]::Normalize([IO.File]::ReadAllText($expectedPath))
    } else { $null }

    try {
        $image = ConvertTo-OcrImage $file.FullName
        $result = $service.RecognizeAsync(
            $image,
            $options,
            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        Write-OcrPreview $file.FullName $result $PreviewDirectory
        $recognized = [QuickTranslate.Core.OcrTextNormalizer]::Normalize(
            (($result.Blocks | ForEach-Object { $_.Text }) -join "`n"))
        $distance = if ($null -ne $expected) {
            Get-LevenshteinDistance $expected $recognized
        } else { $null }
        $denominator = if ($null -ne $expected) { [Math]::Max(1, $expected.Length) } else { $null }
        [pscustomobject]@{
            file = $file.Name
            sha256 = $sha
            width = $image.PixelWidth
            height = $image.PixelHeight
            block_count = $result.Blocks.Count
            used_language = $result.UsedLanguageTag
            language_fallback_used = $result.LanguageFallbackUsed
            text_angle_degrees = $result.TextAngleDegrees
            elapsed_ms = [Math]::Round($result.Elapsed.TotalMilliseconds, 2)
            expected_text_present = $null -ne $expected
            expected_length = if ($null -ne $expected) { $expected.Length } else { $null }
            edit_distance = $distance
            character_error_rate = if ($null -ne $distance) { [Math]::Round($distance / $denominator, 4) } else { $null }
            status = if ($result.Blocks.Count -eq 0) { "no_text" } else { "ok" }
            error_type = $null
        }
    }
    catch {
        [pscustomobject]@{
            file = $file.Name
            sha256 = $sha
            width = $null
            height = $null
            block_count = $null
            used_language = $null
            language_fallback_used = $null
            text_angle_degrees = $null
            elapsed_ms = [Math]::Round($watch.Elapsed.TotalMilliseconds, 2)
            expected_text_present = $null -ne $expected
            expected_length = if ($null -ne $expected) { $expected.Length } else { $null }
            edit_distance = $null
            character_error_rate = $null
            status = "error"
            error_type = $_.Exception.GetType().Name
        }
    }
}

$report = [pscustomobject]@{
    generated_at = [DateTimeOffset]::Now.ToString("o")
    fixture_directory = (Resolve-Path -LiteralPath $FixtureDirectory).Path
    engine = "Windows.Media.Ocr"
    capability_available = $capability.IsAvailable
    capability_reason = $capability.UnavailableReason
    supported_language_count = $capability.SupportedLanguageTags.Count
    max_image_dimension = $capability.MaxImageDimension
    language_hint = $LanguageHint
    allow_language_fallback = [bool]$AllowLanguageFallback
    fixture_count = @($rows).Count
    rows = @($rows)
}

$parent = Split-Path -Parent $OutputPath
if ($parent -and -not (Test-Path -LiteralPath $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$report | Select-Object engine, capability_available, supported_language_count, fixture_count, max_image_dimension
