# Returns the path of Inno Setup 6's compiler (shared by build.ps1 and build-addon.ps1).
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it once with:  winget install JRSoftware.InnoSetup  (or use -NoInstaller)."
}
$iscc
