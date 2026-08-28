# 发布版本号一键升级：csproj + 两个 iss + version.xml 的版本元素。
# <checksum> 依赖安装包构建产物，不在此处理（见 RELEASE.md 步骤 5.0）。
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\release\bump-version.ps1 -Version 1.9.4
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "版本号格式必须为 X.Y.Z，收到: '$Version'"
    exit 1
}

$targets = @(
    (Join-Path $repoRoot "QuickTranslate\QuickTranslate.csproj"),
    (Join-Path $repoRoot "installer\QuickTranslate-setup.iss"),
    (Join-Path $repoRoot "installer\QuickTranslate-setup-full.iss"),
    (Join-Path $repoRoot "installer\version.xml")
)
foreach ($t in $targets) {
    if (-not (Test-Path $t)) { Write-Error "缺失文件: $t"; exit 1 }
}

# .NET Framework 的 ReadAllText/WriteAllText 无 BOM 时按 ANSI 处理，会损坏
# iss 里的中文；这里显式按 UTF-8 读写，并保留各文件原有的 BOM 状态。
function Read-FileText([string]$Path) {
    [System.IO.File]::ReadAllText($Path, [System.Text.Encoding]::UTF8)
}
function Write-FileText([string]$Path, [string]$Content) {
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($hasBom)))
}

# 从 csproj 读取当前版本，作为替换锚点（同时防止重复升级）。
$csprojPath = $targets[0]
$oldVersion = [regex]::Match(
    (Read-FileText $csprojPath),
    '<Version>(\d+\.\d+\.\d+)</Version>').Groups[1].Value
if (-not $oldVersion) { Write-Error "无法从 csproj 解析当前 <Version>"; exit 1 }
if ($oldVersion -eq $Version) {
    Write-Error "csproj 版本已经是 $Version，无需升级（如需重跑请先确认目标版本）。"
    exit 1
}

# 1. csproj：Version / AssemblyVersion / FileVersion
$csproj = Read-FileText $csprojPath
$csproj = [regex]::Replace($csproj, '(<Version>)' + [regex]::Escape($oldVersion) + '(</Version>)', "`${1}$Version`${2}")
$csproj = [regex]::Replace($csproj, '(<AssemblyVersion>)\d+\.\d+\.\d+(\.0</AssemblyVersion>)', "`${1}$Version`${2}")
$csproj = [regex]::Replace($csproj, '(<FileVersion>)\d+\.\d+\.\d+(\.0</FileVersion>)', "`${1}$Version`${2}")
Write-FileText $csprojPath $csproj

# 2. 两个 iss：#define MyAppVersion
#    注意：-replace 的模式参数是一元表达式，内联 "+" 拼接不会并入模式，
#    必须先把完整模式拼进变量（csproj/version.xml 用的是 [regex]::Replace
#    方法调用，不受此限制）。
$issPattern = '#define MyAppVersion "' + [regex]::Escape($oldVersion) + '"'
$issReplacement = "#define MyAppVersion `"$Version`""
foreach ($issPath in $targets[1..2]) {
    $iss = Read-FileText $issPath
    $iss = $iss -replace $issPattern, $issReplacement
    Write-FileText $issPath $iss
}

# 3. version.xml：<version> 与 <url>/<changelog> 内嵌的版本号。
#    只在 url/changelog 元素内部替换，避免误伤注释中的历史版本引用。
$xmlPath = $targets[3]
$xml = Read-FileText $xmlPath
$xml = [regex]::Replace($xml, '<version>' + [regex]::Escape($oldVersion) + '</version>', "<version>$Version</version>")
$xml = [regex]::Replace($xml, '<url>[^<]*</url>|<changelog>[^<]*</changelog>', {
    param($m)
    $m.Value.Replace($oldVersion, $Version)
})
Write-FileText $xmlPath $xml

# 4. 回读校验：新版本必须在，旧版本不得残留在有效元素里。
#    -match 同样是一元表达式参数，模式先拼进变量。
$fail = $false
$csproj = Read-FileText $csprojPath
if ($csproj -notmatch "<Version>$Version</Version>" -or
    $csproj -match "<Version>$oldVersion</Version>") { $fail = $true }
$issNewPattern = '#define MyAppVersion "' + [regex]::Escape($Version) + '"'
foreach ($issPath in $targets[1..2]) {
    $iss = Read-FileText $issPath
    if ($iss -notmatch $issNewPattern) { $fail = $true }
}
$xml = Read-FileText $xmlPath
if ($xml -notmatch "<version>$Version</version>") { $fail = $true }
$urlOldPattern = '<url>[^<]*' + [regex]::Escape($oldVersion) + '[^<]*</url>'
$changelogOldPattern = '<changelog>[^<]*' + [regex]::Escape($oldVersion) + '[^<]*</changelog>'
if ($xml -match $urlOldPattern) { $fail = $true }
if ($xml -match $changelogOldPattern) { $fail = $true }
if ($fail) {
    Write-Error "版本号替换后校验失败，请检查 git diff。"
    exit 1
}

Write-Host ""
Write-Host "版本号已从 $oldVersion 升级到 $Version："
Write-Host "  - QuickTranslate\QuickTranslate.csproj (Version/AssemblyVersion/FileVersion)"
Write-Host "  - installer\QuickTranslate-setup.iss / -full.iss (MyAppVersion)"
Write-Host "  - installer\version.xml (<version>/<url>/<changelog>)"
Write-Host ""
Write-Host "后续步骤（见 docs/RELEASE.md）："
Write-Host "  1. git diff 检查改动"
Write-Host "  2. 编译安装包后回填 version.xml 的 <checksum>（步骤 5.0）"
Write-Host "  3. dotnet test QuickTranslate.Tests\QuickTranslate.Tests.csproj"
