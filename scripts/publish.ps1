<#
.SYNOPSIS
    Builds a portable release of United Sets X: a zip that runs without installing anything
    (.NET and the Windows App SDK are included).
.EXAMPLE
    ./scripts/publish.ps1 -Version 1.0.0
    Creates artifacts/UnitedSetsX-1.0.0-win-x64.zip
#>
param(
    [Parameter(Mandatory)] [string] $Version,
    [ValidateSet("x64", "arm64")] [string] $Platform = "x64"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$runtime = "win-$Platform"
$name = "UnitedSetsX-$Version-$runtime"
$out = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

dotnet publish (Join-Path $root "UnitedSets\UnitedSets.csproj") `
    -c Release `
    -r $runtime `
    -p:Platform=$Platform `
    -p:Version=$Version `
    -p:UnitedSetsPortable=true `
    -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip
Write-Host "Created $zip"
