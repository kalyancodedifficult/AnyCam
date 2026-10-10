<#
.SYNOPSIS
  Developer install of the AnyCam virtual camera from a local build.

.DESCRIPTION
  Copies AnyCamVCam.dll + AnyCamVCamCtl.exe to C:\ProgramData\AnyCam\bin (a folder the Windows
  Frame Server service can read, unlike your user profile), registers the COM class in HKLM
  (one UAC prompt, only when the registered path changes) and creates the "AnyCam" virtual camera
  for the current user.

  A DLL that is still loaded by the Frame Server can't be overwritten, but it can be renamed, so
  old copies are moved aside as AnyCamVCam.dll.old-<n> and cleaned up on later runs.

.PARAMETER BuildDir
  Folder containing AnyCamVCam.dll and AnyCamVCamCtl.exe (default: windows\vcam\build).
#>
param(
    [string]$BuildDir = (Join-Path $PSScriptRoot "..\vcam\build")
)

$ErrorActionPreference = "Stop"
$root = Join-Path $env:ProgramData "AnyCam"
$bin = Join-Path $root "bin"
$logs = Join-Path $root "logs"

New-Item -ItemType Directory -Force $bin, $logs | Out-Null

# LocalService (S-1-5-19) runs the Frame Server: it must be able to load the DLL and write logs.
icacls $root /grant "*S-1-5-19:(OI)(CI)RX" /T /C /Q | Out-Null
icacls $logs /grant "*S-1-5-19:(OI)(CI)M" "*S-1-5-18:(OI)(CI)M" /C /Q | Out-Null

# Remove stale renamed copies (fails silently while they are still loaded).
Get-ChildItem $bin -Filter "*.old-*" -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
}

foreach ($name in "AnyCamVCam.dll", "AnyCamVCamCtl.exe") {
    $source = Join-Path $BuildDir $name
    if (-not (Test-Path $source)) { throw "Missing $source - build windows\vcam first." }
    $target = Join-Path $bin $name
    if (Test-Path $target) {
        try {
            Copy-Item $source $target -Force
            continue
        }
        catch {
            $aside = "$target.old-$([DateTime]::Now.Ticks)"
            Move-Item $target $aside -Force
            Write-Host "Moved in-use $name aside: $aside"
        }
    }
    Copy-Item $source $target -Force
}

$dll = Join-Path $bin "AnyCamVCam.dll"
$clsidKey = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{E39221D4-60F2-41F3-B09B-3156B16205BB}\InprocServer32"
$registered = (Get-ItemProperty -Path $clsidKey -ErrorAction SilentlyContinue).'(default)'
if ($registered -ne $dll) {
    Write-Host "Registering $dll (administrator approval required)..."
    $p = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList "/s", "`"$dll`"" -Verb RunAs -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw "regsvr32 failed with exit code $($p.ExitCode)" }
}
else {
    Write-Host "COM registration already points at $dll"
}

& (Join-Path $bin "AnyCamVCamCtl.exe") install
& (Join-Path $bin "AnyCamVCamCtl.exe") status
