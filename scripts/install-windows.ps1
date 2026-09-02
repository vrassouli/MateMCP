param(
    [string]$Source = (Join-Path $PSScriptRoot 'payload'),
    [switch]$NoStart,
    [switch]$AgentOnly,
    [ValidateSet('Normal','Elevated')]
    [string]$AgentMode = 'Normal'
)

$ErrorActionPreference = 'Stop'

# In a unified Desktop package, install-windows.ps1 is the obvious entry point a
# user will choose. Delegate to the Desktop installer unless this script is being
# invoked internally for the Agent component only.
$DesktopInstaller = Join-Path $PSScriptRoot 'install-desktop-windows.ps1'
$AgentPayload = Join-Path $PSScriptRoot 'agent-payload'
$CompanionPayload = Join-Path $PSScriptRoot 'companion-payload'
if (-not $AgentOnly -and
    (Test-Path $DesktopInstaller) -and
    (Test-Path (Join-Path $AgentPayload 'MateMCP.Agent.exe')) -and
    (Test-Path (Join-Path $CompanionPayload 'MateMCP.Agent.Companion.exe'))) {
    if ($NoStart) { & $DesktopInstaller -NoStart -AgentMode $AgentMode } else { & $DesktopInstaller -AgentMode $AgentMode }
    return
}

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP'
$Bin = Join-Path $Target 'bin'
$Exe = Join-Path $Target 'MateMCP.Agent.exe'
$Shim = Join-Path $Bin 'matemcp.cmd'
$HiddenLauncher = Join-Path $Target 'start-agent-hidden.vbs'
$ConfigureMode = Join-Path $Target 'configure-agent-mode-windows.ps1'

if (-not (Test-Path (Join-Path $Source 'MateMCP.Agent.exe'))) { throw "MateMCP payload not found at: $Source" }

New-Item -ItemType Directory -Force -Path $Target, $Bin | Out-Null
Get-Process 'MateMCP.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Agent and Companion intentionally share the MateMCP root. During an Agent-only
# upgrade the Companion may still be running (for example after the user clicks
# Update now). Never remove the Companion subtree from here: doing so can fail on
# locked binaries and leave the Desktop installation half-removed. The Companion
# installer owns replacement of that subtree and its shortcuts.
$PreserveNames = @('bin', 'Companion', 'uninstall-desktop-windows.ps1')
Get-ChildItem $Target -Force |
    Where-Object { $PreserveNames -notcontains $_.Name } |
    Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source '*') $Target -Recurse -Force

@"
@echo off
"$Exe" %*
"@ | Set-Content -Path $Shim -Encoding ASCII

$escapedExe = $Exe.Replace('"', '""')
$escapedTarget = $Target.Replace('"', '""')
@"
Set shell = CreateObject("WScript.Shell")
shell.CurrentDirectory = "$escapedTarget"
shell.Run Chr(34) & "$escapedExe" & Chr(34), 0, False
"@ | Set-Content -Path $HiddenLauncher -Encoding ASCII

$packageUninstall = Join-Path $PSScriptRoot 'uninstall-windows.ps1'
if (Test-Path $packageUninstall) { Copy-Item $packageUninstall (Join-Path $Target 'uninstall-windows.ps1') -Force }
$packageConfigureMode = Join-Path $PSScriptRoot 'configure-agent-mode-windows.ps1'
if (-not (Test-Path $packageConfigureMode)) { throw "Agent mode configurator not found: $packageConfigureMode" }
Copy-Item $packageConfigureMode $ConfigureMode -Force

$currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($currentUserPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $Bin) {
    $newPath = (($parts + $Bin) -join ';').Trim(';')
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
}
$currentProcessParts = @($env:Path -split ';' | Where-Object { $_ })
if ($currentProcessParts -notcontains $Bin) { $env:Path = (($currentProcessParts + $Bin) -join ';').Trim(';') }

# Configure startup only after payload replacement so upgrades keep the selected
# execution model. Elevated mode intentionally requires an already elevated
# installer process; the Desktop wrapper can request UAC when needed.
& $ConfigureMode -Mode $AgentMode -NoStart

Write-Host 'MateMCP installed/upgraded.'
Write-Host "Binary: $Exe"
Write-Host "Command: $Shim"
Write-Host "Config: $env:APPDATA\MateMCP\appsettings.json"
Write-Host 'Credentials: Windows Credential Manager'
Write-Host "Agent execution mode: $AgentMode"
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$Target\uninstall-windows.ps1`""
Write-Host ''
Write-Host 'The matemcp command is now available in this installer process and in newly opened terminals.'

if (-not $NoStart) {
    & $ConfigureMode -Mode $AgentMode
    Write-Host 'MateMCP Agent started in the configured background mode.'
}
