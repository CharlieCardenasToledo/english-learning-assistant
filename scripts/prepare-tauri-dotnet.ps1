param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Debug", "Release")]
    [string]$Configuration
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot "src-tauri\Cargo.toml"
$sourceDirectory = Join-Path $repositoryRoot "src-tauri\target\dotnet"

if (-not (Test-Path -LiteralPath $sourceDirectory)) {
    throw "No existe la salida del plugin .NET: $sourceDirectory"
}

Push-Location (Split-Path -Parent $manifestPath)
try {
    $metadataJson = cargo metadata --no-deps --format-version 1
    if ($LASTEXITCODE -ne 0) {
        throw "cargo metadata falló con código $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$targetDirectory = ($metadataJson | ConvertFrom-Json).target_directory
$profile = $Configuration.ToLowerInvariant()
$destinationDirectory = Join-Path $targetDirectory "$profile\dotnet"

New-Item -ItemType Directory -Force -Path $destinationDirectory | Out-Null
Copy-Item -Path (Join-Path $sourceDirectory "*") -Destination $destinationDirectory -Recurse -Force

$requiredFiles = @(
    "TauriDotNetBridge.runtimeconfig.json",
    "TauriDotNetBridge.dll",
    "EnglishLearningAssistant.TauriPlugIn.dll",
    "runtimes\win-x64\native\llama.dll",
    "runtimes\win-x64\native\ggml.dll"
)

foreach ($relativePath in $requiredFiles) {
    $resolvedPath = Join-Path $destinationDirectory $relativePath
    if (-not (Test-Path -LiteralPath $resolvedPath)) {
        throw "Falta un recurso requerido en la salida Tauri: $resolvedPath"
    }
}

Write-Host "Plugin .NET preparado en: $destinationDirectory"
