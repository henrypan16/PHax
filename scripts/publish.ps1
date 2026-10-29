<#
.SYNOPSIS
  Builds release binaries of the PHax service and desktop app into .\out.
.EXAMPLE
  .\scripts\publish.ps1 -Runtime win-x64
#>
param(
    [ValidateSet("win-x64", "win-arm64")] [string]$Runtime = "win-x64",
    [string]$Output = (Join-Path $PSScriptRoot "..\out")
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$platform = if ($Runtime -eq "win-arm64") { "ARM64" } else { "x64" }

Write-Host "Running unit and simulation tests..."
dotnet test (Join-Path $root "PHax.Tests") -c Release --filter "Category!=Hardware"
if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }

Write-Host "Publishing service..."
dotnet publish (Join-Path $root "PHax.Service") -c Release -r $Runtime --self-contained true -o (Join-Path $Output "Service")
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }

Write-Host "Publishing desktop app..."
dotnet publish (Join-Path $root "PHax.App") -c Release -r $Runtime -p:Platform=$platform --self-contained true -o (Join-Path $Output "App")
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Copy-Item (Join-Path $PSScriptRoot "install.ps1"), (Join-Path $PSScriptRoot "uninstall.ps1") $Output -Force
Write-Host "Done: $Output"
