# 发布前环境与输入预检。所有 FAIL 项退出码 1；WARN 不阻断但需人工确认。
#
# 用法：powershell -ExecutionPolicy Bypass -File scripts\release\preflight.ps1 [-ExpectedVersion 1.9.4]
param(
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $repoRoot
$script:failures = 0
$script:warnings = 0

function Write-Check([string]$Status, [string]$Name, [string]$Detail) {
    Write-Host ("[{0}] {1}" -f $Status, $Name)
    if ($Detail) { Write-Host ("       {0}" -f $Detail) }
    if ($Status -eq "FAIL") { $script:failures++ }
    if ($Status -eq "WARN") { $script:warnings++ }
}

Write-Host "=== QuickTranslate 发布预检 ==="
Write-Host ""

# 1. .NET SDK
$dotnetOk = $false
try {
    $sdkVersion = (& dotnet --version 2>$null)
    if ($sdkVersion -match '^(\d+)\.') {
        $major = [int]$Matches[1]
        if ($major -ge 8) {
            $dotnetOk = $true
            Write-Check "PASS" ".NET SDK" $sdkVersion
        } else {
            Write-Check "FAIL" ".NET SDK" "需要 8.x，当前 $sdkVersion"
        }
    }
} catch { }
if (-not $dotnetOk) { Write-Check "FAIL" ".NET SDK" "dotnet 不可用" }

# 2. GitHub CLI 与登录状态
if (Get-Command gh -ErrorAction SilentlyContinue) {
    & gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) {
        $account = (& gh api user --jq .login 2>$null)
        Write-Check "PASS" "GitHub CLI 已登录" $account
    } else {
        Write-Check "FAIL" "GitHub CLI 已登录" "gh auth status 失败，先执行 gh auth login"
    }
} else {
    Write-Check "FAIL" "GitHub CLI 已登录" "gh 不在 PATH"
}

# 3. Inno Setup 编译器
$isccPaths = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    (Join-Path $env:USERPROFILE ".dotnet\tools\iscc.exe")
) | Where-Object { $_ -and (Test-Path $_) }
if ($isccPaths) {
    Write-Check "PASS" "Inno Setup (ISCC)" $isccPaths[0]
} else {
    Write-Check "FAIL" "Inno Setup (ISCC)" "未找到 ISCC，安装独立版 https://jrsoftware.org/download.php/is.exe"
}

# 4. SignTool（咨询模式不需要，严格模式必须）
$signtool = Get-Command signtool -ErrorAction SilentlyContinue
if (-not $signtool) {
    $kits = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if ($kits) { $signtool = $kits }
}
if ($signtool) {
    Write-Check "PASS" "SignTool" ([string]$signtool)
} else {
    Write-Check "WARN" "SignTool" "未找到（咨询模式签名可跳过；严格模式必须安装 Windows SDK）"
}

# 5. Git 工作区
$branch = git branch --show-current
$dirty = git status --porcelain
if ($dirty) {
    Write-Check "FAIL" "Git 工作区干净" "有未提交改动：`n$dirty"
} else {
    Write-Check "PASS" "Git 工作区干净" "分支 $branch"
}

# 6. 版本号一致性（csproj / 两个 iss / version.xml）
$csprojText = [System.IO.File]::ReadAllText(
    (Join-Path $repoRoot "QuickTranslate\QuickTranslate.csproj"), [System.Text.Encoding]::UTF8)
$csprojVersion = [regex]::Match($csprojText, '<Version>(\d+\.\d+\.\d+)</Version>').Groups[1].Value
$issVersions = foreach ($name in @("QuickTranslate-setup.iss", "QuickTranslate-setup-full.iss")) {
    [regex]::Match(
        [System.IO.File]::ReadAllText((Join-Path $repoRoot "installer\$name"), [System.Text.Encoding]::UTF8),
        '#define MyAppVersion "(\d+\.\d+\.\d+)"').Groups[1].Value
}
$xmlText = [System.IO.File]::ReadAllText((Join-Path $repoRoot "installer\version.xml"), [System.Text.Encoding]::UTF8)
$xmlVersion = [regex]::Match($xmlText, '<version>(\d+\.\d+\.\d+)</version>').Groups[1].Value
$allVersions = @($csprojVersion) + @($issVersions) + @($xmlVersion)
$distinct = @($allVersions | Select-Object -Unique)
$expected = if ($ExpectedVersion) { $ExpectedVersion } else { $distinct | Select-Object -First 1 }
if ($distinct.Count -eq 1 -and $distinct[0] -eq $expected) {
    Write-Check "PASS" "版本号一致 ($expected)" "csproj / 两个 iss / version.xml"
} else {
    Write-Check "FAIL" "版本号一致" "csproj=$csprojVersion iss=$($issVersions -join ',') version.xml=$xmlVersion (期望 $expected)"
}

# 7. README 结构一致性
& python scripts\update-readme-tree.py --check *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Check "PASS" "README 结构一致"
} else {
    Write-Check "FAIL" "README 结构一致" "执行 python scripts\update-readme-tree.py --write 修复"
}

# 8. 发布词典数据库（正式发布的硬性输入，缺失会导致词典查词功能降级）
$dbPath = Join-Path $repoRoot "QuickTranslate\Data\word-dictionary.db"
if (Test-Path $dbPath) {
    $db = Get-Item $dbPath
    $dbHash = (Get-FileHash $dbPath -Algorithm SHA256).Hash.ToLower()
    Write-Check "PASS" "发布词典存在" ("{0:N1} MB，生成/更新于 {1:yyyy-MM-dd}，SHA256={2}" -f ($db.Length / 1MB), $db.LastWriteTime, $dbHash.Substring(0, 16))
    Write-Host "       [追溯] 发布报告须记录上述完整 SHA256 与日期；与上一版本不一致时必须提供词典源文件重新生成。"
} else {
    Write-Check "FAIL" "发布词典存在" "缺少 QuickTranslate\Data\word-dictionary.db，发布包将只有 AI 查词"
}

# 9. 发布说明草稿
$notesPath = Join-Path $repoRoot "docs\RELEASE_NOTES_NEXT.md"
if (Test-Path $notesPath) {
    $notes = [System.IO.File]::ReadAllText($notesPath, [System.Text.Encoding]::UTF8)
    if ($notes -match [regex]::Escape($expected)) {
        Write-Check "PASS" "RELEASE_NOTES_NEXT.md 基线" "已包含版本 $expected"
    } else {
        Write-Check "WARN" "RELEASE_NOTES_NEXT.md 基线" "未包含版本号 $expected，确认草稿已重置为本版本基线"
    }
} else {
    Write-Check "FAIL" "RELEASE_NOTES_NEXT.md 基线" "缺少 docs\RELEASE_NOTES_NEXT.md"
}

Write-Host ""
Write-Host ("=== 结果：{0} FAIL，{1} WARN ===" -f $script:failures, $script:warnings)
if ($script:failures -gt 0) { exit 1 }
