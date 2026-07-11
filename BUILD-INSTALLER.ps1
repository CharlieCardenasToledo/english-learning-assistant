# ============================================================================
# BUILD-INSTALLER.ps1
# Builds the English Learning Assistant installer (Inno Setup).
#
# Pipeline:
#   1. dotnet publish  -> publish_release\EnglishLearningAssistant.exe
#   2. ISCC            -> installer\Output\EnglishLearningAssistant-<ver>-Setup.exe
#
# The installer itself (installer\EnglishLearningAssistant.iss) optionally
# provisions LM Studio (headless daemon via the official install.ps1) and
# downloads the llama-3.2-3b-instruct model with the lms CLI.
#
# Requires: .NET 8 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup)
# ============================================================================

param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# ── 1. Publish self-contained single-file exe ──────────────────────────────
if (-not $SkipPublish) {
    Write-Host "[1/2] Publishing self-contained exe..." -ForegroundColor Cyan
    dotnet publish $root -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
        -o (Join-Path $root "publish_release")
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

# ── 2. Compile the Inno Setup installer ────────────────────────────────────
Write-Host "[2/2] Compiling Inno Setup installer..." -ForegroundColor Cyan
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup"
}

& $iscc (Join-Path $root "installer\EnglishLearningAssistant.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }

Get-ChildItem (Join-Path $root "installer\Output") -Filter *.exe | ForEach-Object {
    Write-Host ("`nInstaller ready: {0} ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) -ForegroundColor Green
}
