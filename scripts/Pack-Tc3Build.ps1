[CmdletBinding()]
param(
    [string]$Version = "0.3.0",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts"
}

$solution = Join-Path $repoRoot "Tc3Build\Tc3Build.slnx"
$coreProject = Join-Path $repoRoot "Tc3Build.Core\Tc3Build.Core.csproj"
$hostProject = Join-Path $repoRoot "Tc3Build\Tc3Build\Tc3Build.csproj"
$nugetOutput = Join-Path $OutputRoot "nuget"
$publishOutput = Join-Path $OutputRoot "Tc3Build-v$Version"
$zipOutput = Join-Path $OutputRoot "Tc3Build-v$Version-win-x64.zip"

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Force -Path $OutputRoot, $nugetOutput | Out-Null
if (Test-Path -LiteralPath $publishOutput) {
    Remove-Item -LiteralPath $publishOutput -Recurse -Force
}
if (Test-Path -LiteralPath $zipOutput) {
    Remove-Item -LiteralPath $zipOutput -Force
}
Get-ChildItem -LiteralPath $nugetOutput -Filter "Tc3Build.*.nupkg" -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

Write-Host "Restoring solution..."
Invoke-DotNet @("restore", $solution, "-r", "win-x64")

Write-Host "Building solution..."
Invoke-DotNet @("build", $solution, "-c", $Configuration, "-p:Platform=x64", "-p:Version=$Version", "--no-restore")

Write-Host "Running tests..."
Invoke-DotNet @("test", $solution, "-c", $Configuration, "-p:Platform=x64", "--no-build", "--no-restore")

Write-Host "Packing the single Visual Studio NuGet package..."
Invoke-DotNet @("pack", $coreProject, "-c", $Configuration, "-p:Platform=x64", "-p:Version=$Version", "--no-build", "--no-restore", "-o", $nugetOutput)

Write-Host "Publishing the Windows x64 CLI for pipeline use..."
Invoke-DotNet @("publish", $hostProject, "-c", $Configuration, "-r", "win-x64", "--self-contained", "false", "-p:Platform=x64", "-p:Version=$Version", "--no-restore", "-o", $publishOutput)

Compress-Archive -Path (Join-Path $publishOutput "*") -DestinationPath $zipOutput -CompressionLevel Optimal

$package = Get-ChildItem -LiteralPath $nugetOutput -Filter "Tc3Build.$Version.nupkg" -File | Select-Object -First 1
if ($null -eq $package) {
    throw "Expected NuGet package was not created: Tc3Build.$Version.nupkg"
}

Write-Host ""
Write-Host "Created artifacts:"
Write-Host "  NuGet: $($package.FullName)"
Write-Host "  ZIP:   $zipOutput"
Write-Host "  SHA256 (NuGet): $((Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash)"
Write-Host "  SHA256 (ZIP):   $((Get-FileHash -LiteralPath $zipOutput -Algorithm SHA256).Hash)"
