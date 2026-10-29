<#
.SYNOPSIS
  Called by the PHax installer to register, stop or remove the PHax Windows Service.
#>
param(
    [Parameter(Mandatory)] [ValidateSet("install", "stop", "remove")] [string]$Action,
    [string]$InstallDir = $PSScriptRoot
)
$ErrorActionPreference = "Continue"
$name = "PHax"

function Stop-FaxService {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne "Stopped") {
        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
        try { $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30)) } catch { }
    }
    Get-Process -Name "PHax.Service" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

switch ($Action) {
    "stop" { Stop-FaxService }
    "remove" {
        Stop-FaxService
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) { & sc.exe delete $name | Out-Null }
    }
    "install" {
        $exe = Join-Path $InstallDir "Service\PHax.Service.exe"
        $bin = "`"$exe`""
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
            Stop-FaxService
            & sc.exe config $name binPath= $bin start= auto | Out-Null
        } else {
            New-Service -Name $name -BinaryPathName $bin -DisplayName "PHax Fax Service" `
                -Description "Sends and receives faxes through a Class 1 fax modem. Owns the modem COM port." `
                -StartupType Automatic | Out-Null
        }
        # Restart automatically after 10 s, 30 s, 60 s if the service crashes.
        & sc.exe failure $name reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
        Start-Service -Name $name -ErrorAction SilentlyContinue
    }
}
exit 0
