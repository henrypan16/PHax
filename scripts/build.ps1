<#
.SYNOPSIS
  Full release build: makes the icon from logo.png, runs the tests, publishes the service and desktop app into
  .\out, and compiles the installer .\out\installer\PHax-Setup-<version>.exe with Inno Setup 6.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Version 1.2.0 -SkipTests
#>
param(
    [string]$Logo = (Join-Path $PSScriptRoot "..\logo.png"),
    [string]$Version,
    [string]$Output = (Join-Path $PSScriptRoot "..\out"),
    [switch]$SkipTests,
    [switch]$NoInstaller
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Runtime = "win-x64"
if (-not (Test-Path $Logo)) { throw "Logo not found: $Logo" }
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Select-Object -First 1
}
Write-Host "== PHax $Version"

Write-Host "== Generating icon from $Logo"
& (Join-Path $PSScriptRoot "make-icon.ps1") -Logo $Logo -Output (Join-Path $root "PHax.App\Assets\PHax.ico")

if (-not $SkipTests) {
    Write-Host "== Running unit and simulation tests"
    dotnet test (Join-Path $root "PHax.Tests") -c Release --filter "Category!=Hardware"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

Write-Host "== Publishing service"
dotnet publish (Join-Path $root "PHax.Service") -c Release -r $Runtime --self-contained true -p:Version=$Version -o (Join-Path $Output "Service")
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }

Write-Host "== Publishing desktop app"
dotnet publish (Join-Path $root "PHax.App") -c Release -r $Runtime -p:Platform=x64 --self-contained true -p:Version=$Version -o (Join-Path $Output "App")
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

if ($NoInstaller) {
    Copy-Item (Join-Path $PSScriptRoot "install.ps1"), (Join-Path $PSScriptRoot "uninstall.ps1") $Output -Force
    Write-Host "== Done (no installer): $Output"
    return
}

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it once with:  winget install JRSoftware.InnoSetup  (or use -NoInstaller)."
}

Write-Host "== Building installer"
& $iscc /Q "/DAppVersion=$Version" (Join-Path $root "installer\PHax.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
$setup = Join-Path $Output "installer\PHax-Setup-$Version.exe"
Write-Host "== Done: $setup ($([int]((Get-Item $setup).Length / 1MB)) MB)"
Write-Host "   Copy it to the other computer and double-click it."
