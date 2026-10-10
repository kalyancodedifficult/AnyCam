<#
.SYNOPSIS
  Installs the AnyCam virtual camera (Windows 11).

.DESCRIPTION
  Copies AnyCamVCam.dll to "C:\Program Files\AnyCam\VirtualCamera" (the Windows Camera Frame Server
  service can't load DLLs from user folders), registers it in HKLM with regsvr32 and creates the
  "AnyCam" camera for the current user. Needs administrator rights; re-launches itself elevated.

.PARAMETER Source
  Folder containing AnyCamVCam.dll (default: the VirtualCamera folder next to this script).
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "VirtualCamera")
)

$ErrorActionPreference = "Stop"

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Source `"$Source`""
    $process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -PassThru -Wait
    exit $process.ExitCode
}

if ([Environment]::OSVersion.Version.Build -lt 22000) {
    throw "The AnyCam virtual camera needs Windows 11 (build 22000 or newer)."
}

$dllSource = Join-Path $Source "AnyCamVCam.dll"
if (-not (Test-Path $dllSource)) { throw "AnyCamVCam.dll not found in $Source" }

$target = Join-Path $env:ProgramFiles "AnyCam\VirtualCamera"
New-Item -ItemType Directory -Force $target | Out-Null

# Clean up copies that were moved aside by earlier updates (they may still be loaded).
Get-ChildItem $target -Filter "*.old-*" -ErrorAction SilentlyContinue | ForEach-Object {
    Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue
}

foreach ($name in "AnyCamVCam.dll", "AnyCamVCamCtl.exe") {
    $from = Join-Path $Source $name
    if (-not (Test-Path $from)) { continue }
    $to = Join-Path $target $name
    if (Test-Path $to) {
        try { Copy-Item $from $to -Force; continue }
        catch {
            # In use by the Frame Server: a loaded DLL can be renamed, then replaced.
            Move-Item $to "$to.old-$([DateTime]::Now.Ticks)" -Force
        }
    }
    Copy-Item $from $to -Force
    Unblock-File $to -ErrorAction SilentlyContinue
}

$dll = Join-Path $target "AnyCamVCam.dll"
$regsvr = Start-Process -FilePath "$env:SystemRoot\System32\regsvr32.exe" -ArgumentList "/s", "`"$dll`"" -PassThru -Wait
if ($regsvr.ExitCode -ne 0) { throw "regsvr32 failed with exit code $($regsvr.ExitCode)" }

Write-Host "AnyCam virtual camera installed to $target"
exit 0
