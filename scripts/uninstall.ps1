<#
.SYNOPSIS
  Removes the PHax service, program files and Start menu shortcut. Fax data in ProgramData\PHax is kept
  unless -RemoveData is given.
#>
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles "PHax"),
    [switch]$RemoveData
)
$ErrorActionPreference = "Stop"
if (Get-Service -Name "PHax" -ErrorAction SilentlyContinue) {
    Stop-Service "PHax" -Force -ErrorAction SilentlyContinue
    sc.exe delete "PHax" | Out-Null
}
Get-Process -Name "PHax" -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item (Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs\PHax.lnk") -ErrorAction SilentlyContinue
if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
if ($RemoveData) { Remove-Item (Join-Path $env:ProgramData "PHax") -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "PHax removed$(if (-not $RemoveData) { '; fax data kept in ProgramData\PHax' })."
