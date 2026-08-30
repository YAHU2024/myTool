[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath,

    [Parameter(Mandatory = $true)]
    [string]$PythonExecutable,

    [Parameter(Mandatory = $true)]
    [string]$WorkerScriptPath,

    [Parameter(Mandatory = $true)]
    [string]$FixtureDirectory,

    [string]$OutputPath = ".m4-fixture-output\ocr-worker-benchmark.json",

    [string]$LanguageHint,

    [ValidateRange(1, 10)]
    [int]$RunsPerWorkload = 3
)

$ErrorActionPreference = "Stop"
$assemblyFullPath = [IO.Path]::GetFullPath($AssemblyPath)
$pythonFullPath = [IO.Path]::GetFullPath($PythonExecutable)
$workerFullPath = [IO.Path]::GetFullPath($WorkerScriptPath)
$fixtureFullPath = [IO.Path]::GetFullPath($FixtureDirectory)
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
foreach ($path in @($assemblyFullPath, $pythonFullPath, $workerFullPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "文件不存在：$path"
    }
}
if (-not (Test-Path -LiteralPath $fixtureFullPath -PathType Container)) {
    throw "Fixture 目录不存在：$fixtureFullPath"
}

Add-Type -AssemblyName System.Drawing
Add-Type -Path $assemblyFullPath

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

function Get-Percentile([double[]]$values, [double]$percentage) {
    if ($null -eq $values -or $values.Count -eq 0) { return $null }
    $ordered = @($values | Sort-Object)
    if ($ordered.Count -eq 1) { return [double]$ordered[0] }
    $position = ($ordered.Count - 1) * $percentage
    $lower = [Math]::Floor($position)
    $upper = [Math]::Min($lower + 1, $ordered.Count - 1)
    return [double]$ordered[$lower] + ([double]$ordered[$upper] - [double]$ordered[$lower]) * ($position - $lower)
}

function Get-AnnotationBlockCount([System.IO.FileInfo]$image) {
    $annotationPath = [IO.Path]::ChangeExtension($image.FullName, ".json")
    if (-not (Test-Path -LiteralPath $annotationPath -PathType Leaf)) { return $null }
    try {
        $annotation = Get-Content -LiteralPath $annotationPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($annotation.schema -ne "quicktranslate.ocr-fixture.v1") { return $null }
        return @($annotation.blocks).Count
    } catch {
        return $null
    }
}

$images = @(Get-ChildItem -LiteralPath $fixtureFullPath -File | Where-Object { $_.Extension.ToLowerInvariant() -in @('.png', '.jpg', '.jpeg', '.bmp', '.webp') })
if ($images.Count -eq 0) { throw "Fixture 目录没有图片。" }

$targets = @(1, 4, 12, 32)
$workloads = foreach ($target in $targets) {
    $candidates = @(
        $images |
            ForEach-Object {
                $count = Get-AnnotationBlockCount $_
                if ($null -ne $count -and $count -gt 0) {
                    [pscustomobject]@{ Image = $_; GroundTruthBlockCount = [int]$count; Distance = [Math]::Abs([int]$count - $target) }
                }
            } |
            Sort-Object Distance, GroundTruthBlockCount, @{ Expression = { $_.Image.Name } }
    )
    if ($candidates.Count -eq 0) { continue }
    $selected = $candidates[0]
    [pscustomobject]@{
        TargetTextUnitCount = $target
        Fixture = $selected.Image
        GroundTruthBlockCount = $selected.GroundTruthBlockCount
    }
}

$options = [QuickTranslate.Services.RapidOcrWorkerOptions]::new(
    $pythonFullPath,
    $workerFullPath,
    [TimeSpan]::FromSeconds(30),
    [TimeSpan]::FromSeconds(45))
$rows = [System.Collections.Generic.List[object]]::new()
$recognitionOptions = [QuickTranslate.Models.OcrRecognitionOptions]::new($LanguageHint, $true)

foreach ($workload in $workloads) {
    $image = ConvertTo-OcrImage $workload.Fixture.FullName
    try {
        foreach ($phase in @('cold_start', 'warm_worker')) {
            $service = $null
            try {
                if ($phase -eq 'warm_worker') {
                    $service = [QuickTranslate.Services.RapidOcrWorkerService]::new($options)
                }
                for ($run = 1; $run -le $RunsPerWorkload; $run++) {
                    $started = [Diagnostics.Stopwatch]::StartNew()
                    try {
                        if ($phase -eq 'cold_start') {
                            $service = [QuickTranslate.Services.RapidOcrWorkerService]::new($options)
                        }
                        $result = $service.RecognizeAsync(
                            $image,
                            $recognitionOptions,
                            [Threading.CancellationToken]::None).GetAwaiter().GetResult()
                        $started.Stop()
                        $rows.Add([pscustomobject]@{
                            target_text_unit_count = $workload.TargetTextUnitCount
                            fixture = $workload.Fixture.Name
                            ground_truth_block_count = $workload.GroundTruthBlockCount
                            phase = $phase
                            sample_kind = if ($phase -eq 'warm_worker') { if ($run -gt 1) { 'steady_state' } else { 'warm_startup' } } else { 'cold_start' }
                            run = $run
                            elapsed_ms = [Math]::Round($started.Elapsed.TotalMilliseconds, 2)
                            ocr_elapsed_ms = [Math]::Round($result.Elapsed.TotalMilliseconds, 2)
                            recognized_block_count = $result.Blocks.Count
                            status = 'ok'
                            error_type = $null
                        })
                    } catch {
                        $started.Stop()
                        $rows.Add([pscustomobject]@{
                            target_text_unit_count = $workload.TargetTextUnitCount
                            fixture = $workload.Fixture.Name
                            ground_truth_block_count = $workload.GroundTruthBlockCount
                            phase = $phase
                            sample_kind = if ($phase -eq 'warm_worker') { if ($run -gt 1) { 'steady_state' } else { 'warm_startup' } } else { 'cold_start' }
                            run = $run
                            elapsed_ms = [Math]::Round($started.Elapsed.TotalMilliseconds, 2)
                            ocr_elapsed_ms = $null
                            recognized_block_count = $null
                            status = 'error'
                            error_type = $_.Exception.GetType().Name
                        })
                    } finally {
                        if ($phase -eq 'cold_start' -and $null -ne $service) {
                            $service.Dispose()
                            $service = $null
                        }
                    }
                }
            } finally {
                if ($null -ne $service) {
                    $service.Dispose()
                }
            }
        }
    } finally {
        $image = $null
    }
}

$summary = @(
    $rows |
        Group-Object target_text_unit_count, phase, sample_kind |
        ForEach-Object {
            $successful = @($_.Group | Where-Object status -eq 'ok')
            $elapsed = @($successful | ForEach-Object { [double]$_.elapsed_ms })
            [pscustomobject]@{
                target_text_unit_count = [int]$_.Group[0].target_text_unit_count
                phase = [string]$_.Group[0].phase
                sample_kind = [string]$_.Group[0].sample_kind
                fixture = [string]$_.Group[0].fixture
                ground_truth_block_count = [int]$_.Group[0].ground_truth_block_count
                run_count = $_.Count
                success_count = $successful.Count
                error_count = $_.Count - $successful.Count
                median_elapsed_ms = if ($elapsed.Count -gt 0) { [Math]::Round((Get-Percentile $elapsed 0.5), 2) } else { $null }
                p95_elapsed_ms = if ($elapsed.Count -gt 0) { [Math]::Round((Get-Percentile $elapsed 0.95), 2) } else { $null }
            }
        }
)

$report = [pscustomobject]@{
    schema = 'quicktranslate.ocr-worker-benchmark.v1'
    generated_at = [DateTimeOffset]::Now.ToString('o')
    fixture_directory = $fixtureFullPath
    engine = 'rapidocr-onnx-worker'
    runs_per_workload = $RunsPerWorkload
    workload_selection = 'nearest positive JSON ground-truth block count to target 1/4/12/32'
    warm_worker_note = 'warm_worker run 1 includes worker initialization; steady_state excludes it'
    rows = @($rows)
    summary = $summary
}
$outputDirectory = Split-Path -Parent $outputFullPath
if ($outputDirectory) { New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputFullPath -Encoding UTF8
$summary | Format-Table -AutoSize
Write-Output "报告已写入：$outputFullPath"
