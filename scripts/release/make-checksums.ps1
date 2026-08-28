# 生成 Release 附件 SHA256SUMS.txt（sha256sum -c 兼容格式：小写哈希 + 两空格 + 文件名）。
# 生成后在 RELEASE.md 步骤 5.1 中作为第 6 个附件随 Draft Release 上传。
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\release\make-checksums.ps1 -Version 1.9.4
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$releaseDir = Join-Path $repoRoot "publish\releases\v$Version"
if (-not (Test-Path $releaseDir)) { Write-Error "发布目录不存在: $releaseDir"; exit 1 }

$outPath = Join-Path $releaseDir "SHA256SUMS.txt"
$lines = Get-ChildItem $releaseDir -File |
    Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()
        "{0}  {1}" -f $hash, $_.Name
    }

# sha256sum -c 要求 LF 行尾、无 BOM
[System.IO.File]::WriteAllText($outPath, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Host "已生成 $outPath"
Get-Content $outPath
