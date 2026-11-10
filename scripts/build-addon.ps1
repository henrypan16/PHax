<#
.SYNOPSIS
  Builds an add-on installer, .\out\addons\PHax-<Name>-Addon-<version>.exe, for an integration that lives in its own
  (usually private) repository, by default ..\PHax.<Name>.

  The add-on installs into {app}\Addons\<Name> of an existing PHax installation, has its own entry in Windows
  Settings > Apps and is left alone by PHax updates. Run build.ps1 first: files that PHax itself ships (PHax.*,
  framework and shared packages) are left out of the add-on, so it always uses PHax's copies.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\build-addon.ps1 -Name Fillware
  powershell -ExecutionPolicy Bypass -File .\scripts\build-addon.ps1 -Name Fillware -Version 1.2.0 -SkipTests
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^[A-Za-z0-9]+$')] [string]$Name,
    [string]$Path,
    [string]$Version,
    [string]$Output,
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
# Not a parameter default: Windows PowerShell leaves $PSScriptRoot empty there in scripts with mandatory parameters.
if (-not $Output) { $Output = Join-Path $PSScriptRoot "..\out" }
$root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Runtime = "win-x64"
if (-not $Path) { $Path = Join-Path $root "..\PHax.$Name" }
if (-not (Test-Path $Path)) { throw "Add-on source for '$Name' not found: $Path" }
$Path = (Resolve-Path $Path).Path

$appDir = Join-Path $Output "App"
if (-not (Test-Path (Join-Path $appDir "PHax.Core.dll"))) { throw "Build PHax first (.\scripts\build.ps1): $appDir is needed to leave out the files PHax already ships." }
# The add-on is compiled against this PHax, so it asks for at least this version on the target PC.
$phaxVersion = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version | Select-Object -First 1

$project = Get-ChildItem $Path -Recurse -Filter "PHax.Integration.*.csproj" | Where-Object { $_.Name -notlike "*.Tests.csproj" } | Select-Object -First 1
if (-not $project) { throw "No PHax.Integration.*.csproj found under $Path" }
$dllName = [IO.Path]::GetFileNameWithoutExtension($project.Name) + ".dll"
if (-not $Version) { $Version = (dotnet msbuild $project.FullName -nologo -getProperty:Version).Trim() }
if (-not $Version) { throw "Could not read the add-on version; pass -Version." }
Write-Host "== PHax $Name add-on $Version (needs PHax $phaxVersion or later)"

if (-not $SkipTests -and (Get-ChildItem $Path -Recurse -Filter *.Tests.csproj | Select-Object -First 1)) {
    Write-Host "== Running $Name tests"
    dotnet test $Path -c Release
    if ($LASTEXITCODE -ne 0) { throw "$Name tests failed; not packaging." }
}

$work = Join-Path $Output "addons\$Name"
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
$staging = Join-Path $work "publish"
$files = Join-Path $work "files"
Write-Host "== Publishing $($project.Name)"
# No .pdb: the add-on may be private, and symbols would make it easier to read.
dotnet publish $project.FullName -c Release -r $Runtime --self-contained false "-p:Version=$Version" -p:DebugType=none -o $staging
if ($LASTEXITCODE -ne 0) { throw "Add-on publish failed." }

# Only what PHax does not already ship: the add-on's own DLL and its extra dependencies.
New-Item -ItemType Directory -Force $files | Out-Null
Get-ChildItem $staging -File | Where-Object { -not (Test-Path (Join-Path $appDir $_.Name)) } | Copy-Item -Destination $files
Remove-Item $staging -Recurse -Force
if (-not (Test-Path (Join-Path $files $dllName))) { throw "$dllName is missing from the add-on output." }
Write-Host "   Files: $((Get-ChildItem $files -File | ForEach-Object Name) -join ', ')"

$iscc = & (Join-Path $PSScriptRoot "find-iscc.ps1")
Write-Host "== Building add-on installer"
& $iscc /Q "/DAddonName=$Name" "/DAddonVersion=$Version" "/DAddonSource=$files" "/DMinPHaxVersion=$phaxVersion" `
    "/DOutputDir=$(Join-Path $Output 'addons')" (Join-Path $root "installer\PHax-Addon.iss")
if ($LASTEXITCODE -ne 0) { throw "Add-on installer build failed." }
Remove-Item $work -Recurse -Force
$setup = Join-Path $Output "addons\PHax-$Name-Addon-$Version.exe"
Write-Host "== Done: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
Write-Host "   Install PHax first, then run this on the same PC."
