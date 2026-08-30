[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$BundleId = "rapidocr-ppocrv6-small-cpu",
    [string]$BundleVersion = "0.1.0",
    [switch]$CreateArchive,
    [long]$MaxBundleBytes = 2GB
)

$ErrorActionPreference = "Stop"

function Resolve-FullPath([string]$path) {
    return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $path -ErrorAction Stop).Path)
}

if ([string]::IsNullOrWhiteSpace($BundleId) -or $BundleId -notmatch '^[a-z0-9][a-z0-9.-]{1,63}$') {
    throw "BundleId 必须是 2-64 位小写字母、数字、点或连字符。"
}
if ([string]::IsNullOrWhiteSpace($BundleVersion) -or $BundleVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:[-+][0-9A-Za-z.-]+)?$') {
    throw "BundleVersion 必须使用语义化版本格式。"
}
if ($MaxBundleBytes -le 0) {
    throw "MaxBundleBytes 必须为正数。"
}

$sourcePath = Resolve-FullPath $SourceDirectory
if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
    throw "Worker Bundle 源目录不存在：$sourcePath"
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$bundlePath = Join-Path $outputPath "$BundleId-$BundleVersion"
$sourcePrefix = $sourcePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$bundlePrefix = $bundlePath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($bundlePath.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    $sourcePath.StartsWith($bundlePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "OutputDirectory 不能位于 SourceDirectory 内，避免打包结果递归进入源目录。"
}

$workerPath = Join-Path $sourcePath "ocr-worker.py"
$pythonPath = Join-Path $sourcePath "python.exe"
$scriptPythonPath = Join-Path $sourcePath "Scripts\python.exe"
$runtimeManifestPath = Join-Path $sourcePath "runtime-manifest.json"
foreach ($required in @($workerPath, $runtimeManifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Worker Bundle 缺少必需文件：$required"
    }
}
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf) -and
    -not (Test-Path -LiteralPath $scriptPythonPath -PathType Leaf)) {
    throw "Worker Bundle 缺少可移植 Python：python.exe 或 Scripts\python.exe。"
}

try {
    $runtimeManifest = Get-Content -LiteralPath $runtimeManifestPath -Raw | ConvertFrom-Json
} catch {
    throw "runtime-manifest.json 不是有效 JSON。"
}
if ([string]::IsNullOrWhiteSpace([string]$runtimeManifest.engine) -or
    [string]::IsNullOrWhiteSpace([string]$runtimeManifest.model_family)) {
    throw "runtime-manifest.json 缺少 engine 或 model_family。"
}

$files = Get-ChildItem -LiteralPath $sourcePath -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](__pycache__|\.git|\.mypy_cache)([\\/]|$)' } |
    Sort-Object FullName
$totalBytes = [long]0
$entries = foreach ($file in $files) {
    if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Worker Bundle 不允许包含重解析点：$($file.FullName)"
    }
    $relative = [IO.Path]::GetRelativePath($sourcePath, $file.FullName).Replace([IO.Path]::DirectorySeparatorChar, '/')
    if ($relative -eq 'bundle-manifest.json') {
        continue
    }
    $totalBytes += $file.Length
    if ($totalBytes -lt 0) {
        throw "Worker Bundle 文件总大小溢出。"
    }
    if ($totalBytes -gt $MaxBundleBytes) {
        throw "Worker Bundle 超过大小上限：$MaxBundleBytes 字节。"
    }
    [pscustomobject]@{
        path = $relative
        size_bytes = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

if (Test-Path -LiteralPath $bundlePath) {
    throw "输出 Bundle 已存在，为避免覆盖请指定新的 OutputDirectory 或版本：$bundlePath"
}
New-Item -ItemType Directory -Path $bundlePath -Force | Out-Null
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($sourcePath, $file.FullName)
    $destination = Join-Path $bundlePath $relative
    $destinationParent = Split-Path -Parent $destination
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}

$manifest = [ordered]@{
    schema = "quicktranslate.ocr-bundle.v1"
    bundle_id = $BundleId
    bundle_version = $BundleVersion
    engine = [string]$runtimeManifest.engine
    model_family = [string]$runtimeManifest.model_family
    architecture = "win-x64"
    execution_provider = "cpu"
    license_review_status = "pending"
    generated_at = [DateTimeOffset]::Now.ToString("o")
    total_size_bytes = $totalBytes
    runtime_manifest_sha256 = (Get-FileHash -LiteralPath $runtimeManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    files = @($entries)
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $bundlePath "bundle-manifest.json") -Encoding UTF8

if ($CreateArchive) {
    $archivePath = Join-Path $outputPath "$BundleId-$BundleVersion-win-x64.zip"
    Compress-Archive -Path (Join-Path $bundlePath '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Output "bundle_archive=$archivePath"
    Write-Output "bundle_archive_sha256=$archiveHash"
}

Write-Output "bundle_directory=$bundlePath"
Write-Output "bundle_id=$BundleId"
Write-Output "bundle_version=$BundleVersion"
Write-Output "file_count=$(@($entries).Count)"
Write-Output "total_size_bytes=$totalBytes"
Write-Output "license_review_status=pending"
