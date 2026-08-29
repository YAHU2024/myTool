[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,

    [Parameter(Mandatory = $true)]
    [string]$PythonExecutable,

    [Parameter(Mandatory = $true)]
    [string]$WorkerScriptPath,

    [Parameter(Mandatory = $true)]
    [string]$FixturePath,

    [string]$LanguageHint
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) { throw "程序集不存在：$AssemblyPath" }
if (-not (Test-Path -LiteralPath $PythonExecutable -PathType Leaf)) { throw "Python 不存在：$PythonExecutable" }
if (-not (Test-Path -LiteralPath $WorkerScriptPath -PathType Leaf)) { throw "Worker 脚本不存在：$WorkerScriptPath" }
if (-not (Test-Path -LiteralPath $FixturePath -PathType Leaf)) { throw "Fixture 不存在：$FixturePath" }

Add-Type -AssemblyName System.Drawing
Add-Type -Path (Resolve-Path -LiteralPath $AssemblyPath)

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
                [Runtime.InteropServices.Marshal]::Copy($data.Scan0, $payload, 0, $payload.Length)
            } else {
                for ($row = 0; $row -lt $bitmap.Height; $row++) {
                    $source = [IntPtr]::Add($data.Scan0, ($bitmap.Height - 1 - $row) * $data.Stride)
                    [Runtime.InteropServices.Marshal]::Copy($source, $payload, $row * $stride, $stride)
                }
            }
            return [QuickTranslate.Models.OcrImage]::new(
                $bitmap.Width,
                $bitmap.Height,
                $stride,
                [System.ReadOnlyMemory[byte]]::new($payload))
        } finally {
            $bitmap.UnlockBits($data)
        }
    } finally {
        $bitmap.Dispose()
    }
}

$options = [QuickTranslate.Services.RapidOcrWorkerOptions]::new(
    (Resolve-Path -LiteralPath $PythonExecutable).Path,
    (Resolve-Path -LiteralPath $WorkerScriptPath).Path,
    [TimeSpan]::FromSeconds(20),
    [TimeSpan]::FromSeconds(30))
$service = [QuickTranslate.Services.RapidOcrWorkerService]::new($options)
try {
    $capability = $service.Probe()
    $image = ConvertTo-OcrImage $FixturePath
    $result = $service.RecognizeAsync(
        $image,
        [QuickTranslate.Models.OcrRecognitionOptions]::new($LanguageHint, $true),
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    [pscustomobject]@{
        engine = $capability.EngineId
        capability_available = $capability.IsAvailable
        supports_polygons = $capability.SupportsPolygons
        supports_confidence = $capability.SupportsConfidence
        file = [IO.Path]::GetFileName($FixturePath)
        width = $image.PixelWidth
        height = $image.PixelHeight
        block_count = $result.Blocks.Count
        polygon_count = @($result.Blocks | Where-Object { $null -ne $_.Polygon }).Count
        min_confidence = if ($result.Blocks.Count -gt 0) {
            ($result.Blocks | Where-Object { $null -ne $_.Confidence } | Measure-Object Confidence -Minimum).Minimum
        } else { $null }
        elapsed_ms = [Math]::Round($result.Elapsed.TotalMilliseconds, 2)
        used_language = $result.UsedLanguageTag
    }
} finally {
    $service.Dispose()
}
