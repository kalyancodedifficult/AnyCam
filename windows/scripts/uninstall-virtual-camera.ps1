<#
.SYNOPSIS
  Removes the AnyCam virtual camera.

.DESCRIPTION
  Removes the "AnyCam" camera for the current user, unregisters AnyCamVCam.dll and deletes
  "C:\Program Files\AnyCam\VirtualCamera". Re-launches itself elevated when needed.
#>
$ErrorActionPreference = "Stop"

$target = Join-Path $env:ProgramFiles "AnyCam\VirtualCamera"
$dll = Join-Path $target "AnyCamVCam.dll"
$ctl = Join-Path $target "AnyCamVCamCtl.exe"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Remove the per-user camera first, as the current user.
    if (Test-Path $ctl) { & $ctl uninstall | Out-Null }
    $process = Start-Process -FilePath "powershell.exe" -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -Verb RunAs -PassThru -Wait
    exit $process.ExitCode
}

if (Test-Path $dll) {
    Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList "/s", "/u", "`"$dll`"" -Wait
}
Remove-Item $target -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "AnyCam virtual camera removed. (A copy still loaded by the camera service is deleted after a restart.)"
exit 0
