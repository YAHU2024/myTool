# RELEASE.md 3.3 编译后完整性校验。
#
# 背景：v1.9.2 曾发生安装包被后处理工具破坏（版本资源被清空、数据截断、
# Inno Setup CRC 失效，运行时报 "The setup files are corrupted"）。
# 本项目 Inno Setup 产物的版本资源是固定宽度填充（健康基线即如此，
# 参见 .build-output/versioninfo.txt 的调查记录），因此按 Trim 后比较；
# 损坏特征是字段被清空，用 ProductName/FileDescription 非空兜底。
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\release\verify-setup.ps1 -Version 1.9.4
# 任一断言失败退出码 1，禁止上传对应安装包。
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$files = @(
    (Join-Path $repoRoot "publish\releases\v$Version\QuickTranslate-Setup-$Version-win-x64.exe"),
    (Join-Path $repoRoot "publish\releases\v$Version\QuickTranslate-Setup-$Version-win-x64-full.exe")
)
foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Error "缺失: $f"; exit 1 }
    $vi = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $f))

    $pv = $vi.ProductVersion.Trim()
    $cn = $vi.CompanyName.Trim()
    if ($pv -ne $Version) {
        Write-Error "完整性校验失败: $f ProductVersion='$pv' (期望 $Version)。安装包可能被后处理工具修改，禁止发布。"
        exit 1
    }
    if ($cn -ne "YaHu") {
        Write-Error "完整性校验失败: $f CompanyName='$cn' (期望 YaHu)。"
        exit 1
    }
    if ([string]::IsNullOrWhiteSpace($vi.ProductName) -or
        [string]::IsNullOrWhiteSpace($vi.FileDescription)) {
        Write-Error "完整性校验失败: $f ProductName/FileDescription 为空（v1.9.2 事故的损坏特征）。"
        exit 1
    }
    $sizeMB = [math]::Round((Get-Item $f).Length / 1MB, 1)
    Write-Host ("  PASS: {0} ({1} MB, ProductVersion={2}, CompanyName={3})" -f $f, $sizeMB, $pv, $cn)
}

Write-Host ""
Write-Host "完整版安装包 SHA256（回填 version.xml 的 <checksum> 用）："
(Get-FileHash $files[1] -Algorithm SHA256).Hash
