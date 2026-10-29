<#
.SYNOPSIS
  Installs PHax from a publish folder (see publish.ps1). Run from an elevated PowerShell.
  - Copies the service and app to Program Files\PHax
  - Creates the "PHax" Windows Service (automatic start, restart on failure)
  - Grants local users modify access to ProgramData\PHax (shared database and documents)
  - Adds a Start menu shortcut
#>
param(
    [string]$Source = $PSScriptRoot,
    [string]$InstallDir = (Join-Path $env:ProgramFiles "PHax")
)
$ErrorActionPreference = "Stop"
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell."
}

$serviceName = "PHax"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Stopping existing service..."
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
}

Write-Host "Copying files to $InstallDir..."
New-Item -ItemType Directory -Force (Join-Path $InstallDir "Service"), (Join-Path $InstallDir "App") | Out-Null
Copy-Item (Join-Path $Source "Service\*") (Join-Path $InstallDir "Service") -Recurse -Force
Copy-Item (Join-Path $Source "App\*") (Join-Path $InstallDir "App") -Recurse -Force

$data = Join-Path $env:ProgramData "PHax"
New-Item -ItemType Directory -Force $data | Out-Null
# Users (S-1-5-32-545) need modify rights: the app writes drafts and settings to the shared database.
icacls $data /grant "*S-1-5-32-545:(OI)(CI)M" /T /Q | Out-Null

$exe = Join-Path $InstallDir "Service\PHax.Service.exe"
if (-not $existing) {
    Write-Host "Creating service..."
    New-Service -Name $serviceName -BinaryPathName "`"$exe`"" -DisplayName "PHax Fax Service" `
        -Description "Sends and receives faxes through a Class 1 fax modem. Owns the modem COM port." -StartupType Automatic | Out-Null
} else {
    sc.exe config $serviceName binPath= "`"$exe`"" start= auto | Out-Null
}
# Restart after 10 s, 30 s, 60 s on failure.
sc.exe failure $serviceName reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null

$shortcutDir = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $shortcutDir "PHax.lnk"))
$lnk.TargetPath = Join-Path $InstallDir "App\PHax.exe"
$lnk.WorkingDirectory = Join-Path $InstallDir "App"
$icon = Join-Path $InstallDir "App\Assets\PHax.ico"
if (Test-Path $icon) { $lnk.IconLocation = "$icon,0" }
$lnk.Save()

Start-Service $serviceName
Write-Host "PHax installed. Open PHax from the Start menu and choose the modem in Settings > Modem."
