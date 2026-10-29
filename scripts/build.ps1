<#
.SYNOPSIS
  Full release build: makes the icon from logo.png, runs the tests, publishes the service and desktop app into
  .\out, and compiles the installer .\out\installer\PHax-Setup-<version>.exe with Inno Setup 6.

  -Edition <Name> builds an edition with a private integration: the project in PHax.<Name> (a separate,
  private repository in the folder next to this one) is published into the desktop app folder and the installer is named
  PHax-<Name>-Setup-<version>.exe. Without -Edition the standard build contains no integration.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
  powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -Version 1.2.0 -SkipTests
#>
param(
    [string]$Logo = (Join-Path $PSScriptRoot "..\logo.png"),
    [string]$Version,
    [string]$Output = (Join-Path $PSScriptRoot "..\out"),
    [switch]$SkipTests,
    [switch]$NoInstaller,
    [ValidatePattern('^[A-Za-z0-9]+$')] [string]$Edition,
    [string]$IntegrationPath
)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Runtime = "win-x64"
if (-not (Test-Path $Logo)) { throw "Logo not found: $Logo" }
if ($Edition) {
    if (-not $IntegrationPath) { $IntegrationPath = Join-Path $root "..\PHax.$Edition" }
    if (-not (Test-Path $IntegrationPath)) { throw "Integration for edition '$Edition' not found: $IntegrationPath" }
    $IntegrationPath = (Resolve-Path $IntegrationPath).Path
}
if (-not $Version) {
    $Version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Select-Object -First 1
}
Write-Host "== PHax $(if ($Edition) { "$Edition " })$Version"

Write-Host "== Generating icon from $Logo"
& (Join-Path $PSScriptRoot "make-icon.ps1") -Logo $Logo -Output (Join-Path $root "PHax.App\Assets\PHax.ico")

if (-not $SkipTests) {
    Write-Host "== Running unit and simulation tests"
    dotnet test (Join-Path $root "PHax.Tests") -c Release --filter "Category!=Hardware"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; not publishing." }
    if ($Edition -and (Get-ChildItem $IntegrationPath -Recurse -Filter *.Tests.csproj | Select-Object -First 1)) {
        Write-Host "== Running $Edition integration tests"
        dotnet test $IntegrationPath -c Release
        if ($LASTEXITCODE -ne 0) { throw "$Edition integration tests failed; not publishing." }
    }
}

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

Write-Host "== Publishing service"
dotnet publish (Join-Path $root "PHax.Service") -c Release -r $Runtime --self-contained true -p:Version=$Version -o (Join-Path $Output "Service")
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }

Write-Host "== Publishing desktop app"
dotnet publish (Join-Path $root "PHax.App") -c Release -r $Runtime -p:Platform=x64 --self-contained true -p:Version=$Version -o (Join-Path $Output "App")
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

if ($Edition) {
    Write-Host "== Publishing $Edition integration"
    $project = Get-ChildItem $IntegrationPath -Recurse -Filter "PHax.Integration.*.csproj" | Select-Object -First 1
    if (-not $project) { throw "No PHax.Integration.*.csproj found under $IntegrationPath" }
    $staging = Join-Path $Output "integration"
    # No .pdb: the integration may be private, and symbols would make it easier to read.
    dotnet publish $project.FullName -c Release -r $Runtime --self-contained false -p:Version=$Version -p:DebugType=none -o $staging
    if ($LASTEXITCODE -ne 0) { throw "Integration publish failed." }
    # Only add what the app does not already ship (the integration's own DLL and its extra dependencies), so the
    # integration can never replace a PHax or framework assembly.
    $appDir = Join-Path $Output "App"
    Get-ChildItem $staging -File | Where-Object { -not (Test-Path (Join-Path $appDir $_.Name)) } |
        Copy-Item -Destination $appDir
    Remove-Item $staging -Recurse -Force
}

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
$isccArgs = @("/Q", "/DAppVersion=$Version")
if ($Edition) { $isccArgs += "/DEdition=$Edition" }
& $iscc @isccArgs (Join-Path $root "installer\PHax.iss")
if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
$setup = Join-Path $Output "installer\PHax-$(if ($Edition) { "$Edition-" })Setup-$Version.exe"
Write-Host "== Done: $setup ($([int]((Get-Item $setup).Length / 1MB)) MB)"
Write-Host "   Copy it to the other computer and double-click it."
