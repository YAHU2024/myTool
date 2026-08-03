[CmdletBinding()]
param(
    [string]$EcdictPath,
    [string]$OewnPath,
    [string]$OutputPath
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

if ([string]::IsNullOrWhiteSpace($EcdictPath)) {
    $EcdictPath = Join-Path $repoRoot ".build-output\word-dict-mini\ecdict.csv"
}
if ([string]::IsNullOrWhiteSpace($OewnPath)) {
    $OewnPath = Join-Path $repoRoot ".build-output\word-dict-mini\oewn-2025-json.zip"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "QuickTranslate\Data\word-dictionary.db"
}

$expectedEcdictHash = "1a6947e04785db63613a92e14903cdae7954f7e84860b10e68e5c7cbb3f9c3cf"
$expectedOewnHash = "7d749f6e2c39e6970e4997839dcf6e42fd281f3c2fae0171d2192bae8cfa4b51"

function Assert-SourceHash {
    param(
        [string]$Path,
        [string]$ExpectedHash,
        [string]$SourceName
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$SourceName source file was not found: $Path"
    }

    $actualHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne $ExpectedHash) {
        throw "$SourceName SHA-256 mismatch. Expected $ExpectedHash, got $actualHash."
    }
}

Assert-SourceHash -Path $EcdictPath -ExpectedHash $expectedEcdictHash -SourceName "ECDICT"
Assert-SourceHash -Path $OewnPath -ExpectedHash $expectedOewnHash -SourceName "OEWN 2025"

$outputDirectory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$reportPath = Join-Path $repoRoot ".build-output\word-dict-mini\release-report.json"
$importerPath = Join-Path $PSScriptRoot "build-word-dictionary-mini.py"

& python $importerPath `
    --ecdict $EcdictPath `
    --oewn $OewnPath `
    --output $OutputPath `
    --report $reportPath
if ($LASTEXITCODE -ne 0) {
    throw "Word dictionary importer failed with exit code $LASTEXITCODE."
}

Write-Host "Release dictionary prepared: $OutputPath"
Write-Host "Validation report: $reportPath"
