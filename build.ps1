<#
.SYNOPSIS
    Builds PULSE//WATCH and publishes a single-file exe to .\dist.

.EXAMPLE
    .\build.ps1                 # framework-dependent single exe (needs .NET 8 runtime on target)
    .\build.ps1 -SelfContained  # portable exe, no .NET install needed on target (~70 MB larger)
    .\build.ps1 -DebugBuild     # plain Debug build only, no publish
#>
param(
    [switch]$SelfContained,
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# Locate dotnet even if PATH hasn't been refreshed since the SDK install.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $fallback = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path $fallback) { $dotnet = $fallback }
    else { throw ".NET SDK not found. Install with: winget install Microsoft.DotNet.SDK.8" }
} else {
    $dotnet = $dotnet.Source
}

if ($DebugBuild) {
    & $dotnet build ConnectionChecker.csproj -c Debug --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    Write-Host "`nDebug build: bin\Debug\net8.0-windows\PulseWatch.exe" -ForegroundColor Cyan
    exit 0
}

$publishArgs = @(
    "publish", "ConnectionChecker.csproj",
    "-c", "Release",
    "-r", "win-x64",
    "-o", "dist",
    "--nologo",
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "--self-contained", $(if ($SelfContained) { "true" } else { "false" })
)

& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$exe = Join-Path $PSScriptRoot "dist\PulseWatch.exe"
$size = "{0:N1} MB" -f ((Get-Item $exe).Length / 1MB)
Write-Host "`nPublished: $exe ($size)" -ForegroundColor Cyan
