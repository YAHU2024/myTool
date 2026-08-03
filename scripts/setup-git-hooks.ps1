# Setup pre-commit hook to run dotnet format check before each commit.
# Run: .\scripts\setup-git-hooks.ps1

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$hookPath = Join-Path $repoRoot ".git\hooks\pre-commit"

# The hook script content (LF line endings required for bash).
$hookContent = @'
#!/bin/bash
set -e
echo "Running dotnet format check..."
dotnet format "$(dirname "$0")/../../QuickTranslate/QuickTranslate.csproj" --verify-no-changes
echo "[INFO] format check passed."
'@

if (Test-Path $hookPath) {
    Write-Host "pre-commit hook already exists, overwriting..." -ForegroundColor Yellow
}

# Write with LF line endings (bash requires LF, not CRLF).
[System.IO.File]::WriteAllText($hookPath, $hookContent.Replace("`r`n", "`n").Replace("`r", "`n") + "`n", [System.Text.UTF8Encoding]::new($false))

Write-Host "pre-commit hook installed at .git/hooks/pre-commit" -ForegroundColor Green
exit 0
