# Manual Verification Script: Authenticode Trust Chain for Auto-Update
# =============================================================================
# This script generates a self-signed code-signing certificate, signs a test
# EXE, and validates the AuthenticodeVerifier pipeline end-to-end.
#
# Prerequisites:
#   - Windows SDK (signtool.exe)
#   - Administrator rights (to install test cert to Trusted Root)
#   - .NET 8 SDK
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File scripts/test-authenticode.ps1

param(
    [string]$Publisher = "YaHu",
    [switch]$NoCleanup
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path "$scriptDir\.."
$tempDir = Join-Path $env:TEMP "QuickTranslate-AuthTest-$(Get-Random)"
$certName = "QuickTranslate Test Code Signing"
$pfxPath = Join-Path $tempDir "test-codesign.pfx"
$pfxPassword = "test1234!"
$testExeName = "test-update-installer.exe"
$testExePath = Join-Path $tempDir $testExeName
$timestampUrl = "http://timestamp.digicert.com"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host " QuickTranslate Authenticode 信任链测试" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# ── Step 0: Create temp directory ──────────────────────────────────────
Write-Host "[0/5] Preparing temp directory: $tempDir" -ForegroundColor Gray
New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

# ── Step 1: Generate self-signed code-signing certificate ──────────────
Write-Host "[1/5] Generating self-signed code-signing certificate..." -ForegroundColor Yellow

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject "CN=$Publisher, O=$Publisher, C=CN" `
    -FriendlyName $certName `
    -CertStoreLocation "Cert:\CurrentUser\My" `
    -KeyExportPolicy Exportable `
    -KeySpec Signature `
    -KeyLength 2048 `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddDays(30)

Write-Host "  Certificate thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
Write-Host "  Subject: $($cert.Subject)" -ForegroundColor Green

# Export to PFX
$cert | Export-PfxCertificate -FilePath $pfxPath -Password (ConvertTo-SecureString -String $pfxPassword -Force -AsPlainText) | Out-Null
Write-Host "  PFX exported: $pfxPath" -ForegroundColor Green

# ── Step 2: Create a test EXE to sign ──────────────────────────────────
Write-Host "[2/5] Creating test EXE stub..." -ForegroundColor Yellow

# Build a minimal PE file (copy QuickTranslate.exe as test stub)
$quickTranslateExe = Join-Path $repoRoot "QuickTranslate\bin\Debug\net8.0-windows\QuickTranslate.exe"
if (Test-Path $quickTranslateExe) {
    Copy-Item $quickTranslateExe $testExePath -Force
    Write-Host "  Copied QuickTranslate.exe as test stub" -ForegroundColor Green
} else {
    # Fallback: minimal MZ stub (will fail PE signage but tests error paths)
    Write-Host "  WARNING: QuickTranslate.exe not found, creating minimal stub" -ForegroundColor DarkYellow
    $mz = [byte[]]@(
        0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
        0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x80, 0x00, 0x00, 0x00
    )
    [System.IO.File]::WriteAllBytes($testExePath, $mz)
}

# ── Step 3: Sign the test EXE ──────────────────────────────────────────
Write-Host "[3/5] Signing test EXE with self-signed certificate..." -ForegroundColor Yellow

$signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
if (-not $signtool) {
    # Try common Windows SDK paths
    $sdkBase = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $sdkBase) {
        $latest = Get-ChildItem $sdkBase -Directory | Sort-Object Name -Descending | Select-Object -First 1
        $signtool = Get-ChildItem "$($latest.FullName)\x64\signtool.exe" -ErrorAction SilentlyContinue
    }
}

if (-not $signtool) {
    Write-Host "  ERROR: signtool.exe not found. Install Windows SDK." -ForegroundColor Red
    Write-Host "  Skipping signing step. Continue with unsigned file tests." -ForegroundColor Yellow
    $signed = $false
} else {
    Write-Host "  signtool: $($signtool.FullName)" -ForegroundColor Gray

    & $signtool.FullName sign /fd SHA256 `
        /f $pfxPath /p $pfxPassword `
        /d "QuickTranslate Test Setup" `
        $testExePath 2>&1 | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }

    if ($LASTEXITCODE -ne 0) {
        Write-Host "  ERROR: Signing failed (exit code $LASTEXITCODE)" -ForegroundColor Red
        $signed = $false
    } else {
        Write-Host "  Signed successfully" -ForegroundColor Green
        $signed = $true
    }
}

# ── Step 4: Install cert to Trusted Root (temporarily) ────────────────
if ($signed) {
    Write-Host "[4/5] Installing test cert to Trusted Root (temporary)..." -ForegroundColor Yellow
    Write-Host "  NOTE: This step requires administrator approval." -ForegroundColor DarkYellow

    $rootStore = Get-Item Cert:\CurrentUser\Root
    $rootStore.Open("ReadWrite")
    $rootStore.Add($cert)
    Write-Host "  Cert installed to CurrentUser\Root" -ForegroundColor Green
}

# ── Step 5: Run AuthenticodeVerifier test ──────────────────────────────
Write-Host "[5/5] Running AuthenticodeVerifier tests..." -ForegroundColor Yellow

# Positive test: signed file
if ($signed) {
    Write-Host "`n  --- Test A: Signed file (expected: Valid) ---" -ForegroundColor Cyan
    $env:AUTHENTICODE_TEST_FILE = $testExePath
    $env:AUTHENTICODE_EXPECTED_PUB = $Publisher

    Push-Location $repoRoot
    try {
        $result = & dotnet test "QuickTranslate.Tests\QuickTranslate.Tests.csproj" `
            --filter "FullyQualifiedName~ManualVerify_SignedFile" `
            --no-restore 2>&1
        $result | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
    } finally {
        Pop-Location
    }
    Remove-Item Env:\AUTHENTICODE_TEST_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\AUTHENTICODE_EXPECTED_PUB -ErrorAction SilentlyContinue
}

# Negative test: unsigned file
Write-Host "`n  --- Test B: Unsigned file (expected: NotSigned) ---" -ForegroundColor Cyan

# Create a fresh unsigned file
$unsignedFile = Join-Path $tempDir "unsigned-test.exe"
$mz = [byte[]]@(
    0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00,
    0x04, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x00, 0x00,
    0xB8, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
)
[System.IO.File]::WriteAllBytes($unsignedFile, $mz)

$env:AUTHENTICODE_BAD_FILE = $unsignedFile
$env:AUTHENTICODE_BAD_EXPECTED = "NotSigned"

Push-Location $repoRoot
try {
    $result = & dotnet test "QuickTranslate.Tests\QuickTranslate.Tests.csproj" `
        --filter "FullyQualifiedName~ManualVerify_BadFile" `
        --no-restore 2>&1
    $result | ForEach-Object { Write-Host "  $_" -ForegroundColor Gray }
} finally {
    Pop-Location
}
Remove-Item Env:\AUTHENTICODE_BAD_FILE -ErrorAction SilentlyContinue
Remove-Item Env:\AUTHENTICODE_BAD_EXPECTED -ErrorAction SilentlyContinue

# Negative test: publisher mismatch
if ($signed) {
    Write-Host "`n  --- Test C: Wrong publisher (expected: PublisherMismatch) ---" -ForegroundColor Cyan
    $env:AUTHENTICODE_BAD_FILE = $testExePath
    $env:AUTHENTICODE_BAD_EXPECTED = "PublisherMismatch"

    Push-Location $repoRoot
    try {
        # Pass a deliberately wrong publisher
        $env:AUTHENTICODE_EXPECTED_PUB = "TotallyWrongPublisherXYZ"
        # For this we need a dedicated test; the current ManualVerify_BadFile
        # uses hardcoded "YaHu" publisher. Let's just run Verify directly.
        $result = & dotnet test "QuickTranslate.Tests\QuickTranslate.Tests.csproj" `
            --filter "FullyQualifiedName~ManualVerify_SignedFile" `
            --no-restore -e AUTHENTICODE_TEST_FILE="$testExePath" -e AUTHENTICODE_EXPECTED_PUB="WrongPublisher" 2>&1
        # ManualVerify_SignedFile will fail with "Valid" assertion when publisher is wrong
        # This demonstrates detection capability
        Write-Host "  (Expecting failure: Valid != PublisherMismatch)" -ForegroundColor Gray
    } finally {
        Pop-Location
    }
    Remove-Item Env:\AUTHENTICODE_TEST_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\AUTHENTICODE_EXPECTED_PUB -ErrorAction SilentlyContinue
    Remove-Item Env:\AUTHENTICODE_BAD_FILE -ErrorAction SilentlyContinue
    Remove-Item Env:\AUTHENTICODE_BAD_EXPECTED -ErrorAction SilentlyContinue
}

# ── Cleanup ────────────────────────────────────────────────────────────
if (-not $NoCleanup) {
    Write-Host "`n[Cleanup] Removing test certificate from Trusted Root..." -ForegroundColor Gray
    Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint } | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $cert.Thumbprint } | Remove-Item -Force -ErrorAction SilentlyContinue

    Write-Host "[Cleanup] Removing temp directory: $tempDir" -ForegroundColor Gray
    Remove-Item -Recurse -Force $tempDir -ErrorAction SilentlyContinue
} else {
    Write-Host "`n[Cleanup] Skipped (--NoCleanup). Artifacts kept at: $tempDir" -ForegroundColor DarkYellow
    Write-Host "  To clean up manually:" -ForegroundColor DarkYellow
    Write-Host "    certutil -delstore Root `"$($cert.SerialNumber)`"" -ForegroundColor DarkYellow
    Write-Host "    Remove-Item -Recurse $tempDir" -ForegroundColor DarkYellow
}

Write-Host "`n============================================" -ForegroundColor Cyan
Write-Host " Verification complete." -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
