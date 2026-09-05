[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot "..\ocr-runtime"),
    [string]$PythonCommand = "py",
    [string]$PythonVersion = "3.11",
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
$scriptPythonPath = Join-Path $destinationPath "Scripts\python.exe"
if (-not (Test-Path -LiteralPath $pythonPath -PathType Leaf) -and
    -not (Test-Path -LiteralPath $scriptPythonPath -PathType Leaf)) {
    $launcherArguments = @()
    if ([IO.Path]::GetFileNameWithoutExtension($PythonCommand) -ieq "py") {
        $launcherArguments += "-$PythonVersion"
    }
    & $PythonCommand @launcherArguments -m venv $destinationPath
    if ($LASTEXITCODE -ne 0) {
        throw "创建 OCR Python $PythonVersion 虚拟环境失败，退出码：$LASTEXITCODE"
    }
}

$runtimePythonPath = if (Test-Path -LiteralPath $pythonPath -PathType Leaf) {
    $pythonPath
} else {
    $scriptPythonPath
}
if (-not (Test-Path -LiteralPath $runtimePythonPath -PathType Leaf)) {
    throw "OCR Python 虚拟环境缺少解释器。"
}
$runtimePythonVersion = (& $runtimePythonPath -c "import sys; print(f'{sys.version_info.major}.{sys.version_info.minor}')").Trim()
if ($LASTEXITCODE -ne 0 -or $runtimePythonVersion -ne $PythonVersion) {
    throw "OCR 运行时需要 Python $PythonVersion，当前为 $runtimePythonVersion。请更换 Destination，或先人工移除不兼容的隔离运行时。"
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

$runtimeVersions = & $runtimePythonPath -c "import importlib.metadata as m; import rapidocr, onnxruntime, cv2, numpy; print(m.version('rapidocr')); print(m.version('onnxruntime'))"
if ($LASTEXITCODE -ne 0 -or $runtimeVersions.Count -lt 2) {
    throw "OCR 运行时包导入验证失败。"
}

Copy-Item -LiteralPath $workerPath -Destination (Join-Path $destinationPath "ocr-worker.py") -Force
$manifest = [pscustomobject]@{
    engine = "RapidOCR ONNX Runtime"
    model_family = "externally-managed"
    python_version = $runtimePythonVersion
    rapidocr_version = [string]$runtimeVersions[0]
    onnxruntime_version = [string]$runtimeVersions[1]
    installed_at = [DateTimeOffset]::Now.ToString("o")
    source = "scripts/install-ocr-runtime.ps1"
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destinationPath "runtime-manifest.json") -Encoding UTF8
Write-Output "OCR 本地运行时已安装：$destinationPath"
Write-Output "应用将通过 $runtimePythonPath 自动发现运行时；模型由用户在设置页主动下载并校验。"
