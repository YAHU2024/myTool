[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\ocr-runtime"),
    [string]$PythonCommand = "py",
    [switch]$Offline
)

$ErrorActionPreference = "Stop"
$destinationPath = [IO.Path]::GetFullPath($Destination)
$workerPath = Join-Path $PSScriptRoot "ocr-worker.py"
if (-not (Test-Path -LiteralPath $workerPath -PathType Leaf)) {
    throw "OCR Worker 脚本不存在：$workerPath"
}

New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
$pythonPath = Join-Path $destinationPath "python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf)) {
    & $PythonCommand -m venv $destinationPath
    if ($LASTEXITCODE -ne 0) {
        throw "创建 OCR Python 虚拟环境失败，退出码：$LASTEXITCODE"
    }
}

$pip = Join-Path $destinationPath "Scripts\pip.exe"
if (-not (Test-Path -LiteralPath $pip -PathType Leaf)) {
    throw "OCR Python 虚拟环境缺少 pip：$pip"
}

$packages = @(
    "rapidocr==3.9.2",
    "onnxruntime==1.27.0"
)
$pipArguments = @("install", "--disable-pip-version-check")
if ($Offline) {
    $pipArguments += "--no-index"
}
$pipArguments += $packages
& $pip @pipArguments
if ($LASTEXITCODE -ne 0) {
    throw "安装 OCR 模型运行时失败，退出码：$LASTEXITCODE"
}

Copy-Item -LiteralPath $workerPath -Destination (Join-Path $destinationPath "ocr-worker.py") -Force
$manifest = [pscustomobject]@{
    engine = "RapidOCR ONNX Runtime"
    model_family = "PP-OCRv6 small"
    rapidocr_version = "3.9.2"
    onnxruntime_version = "1.27.0"
    installed_at = [DateTimeOffset]::Now.ToString("o")
    source = "scripts/install-ocr-runtime.ps1"
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destinationPath "runtime-manifest.json") -Encoding UTF8
Write-Output "OCR 本地运行时已安装：$destinationPath"
Write-Output "模型首次启动不需要联网；应用将通过 ocr-runtime\python.exe 自动发现。"
