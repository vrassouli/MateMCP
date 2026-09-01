param(
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$AgentPayload = Join-Path $PSScriptRoot 'agent-payload'
$CompanionPayload = Join-Path $PSScriptRoot 'companion-payload'
$AgentInstaller = Join-Path $PSScriptRoot 'install-windows.ps1'
$CompanionInstaller = Join-Path $PSScriptRoot 'install-companion-windows.ps1'

if (-not (Test-Path $AgentInstaller)) { throw "Agent installer not found: $AgentInstaller" }
if (-not (Test-Path $CompanionInstaller)) { throw "Companion installer not found: $CompanionInstaller" }

# Agent installation intentionally runs first because it refreshes %LOCALAPPDATA%\MateMCP.
# -AgentOnly prevents install-windows.ps1 from delegating back to this Desktop wrapper.
& $AgentInstaller -Source $AgentPayload -NoStart -AgentOnly
& $CompanionInstaller -Source $CompanionPayload -NoStart

$InstalledRoot = Join-Path $env:LOCALAPPDATA 'MateMCP'
Copy-Item (Join-Path $PSScriptRoot 'uninstall-desktop-windows.ps1') (Join-Path $InstalledRoot 'uninstall-desktop-windows.ps1') -Force

Write-Host ''
Write-Host 'MateMCP Desktop installed/upgraded.'
Write-Host 'Components: background Agent + on-demand Agent Companion'
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$InstalledRoot\uninstall-desktop-windows.ps1`""

if (-not $NoStart) {
    $HiddenLauncher = Join-Path $InstalledRoot 'start-agent-hidden.vbs'
    $WScript = Join-Path $env:WINDIR 'System32\wscript.exe'
    $CompanionExe = Join-Path $InstalledRoot 'Companion\MateMCP.Agent.Companion.exe'
    Start-Process -FilePath $WScript -ArgumentList "`"$HiddenLauncher`""
    Start-Sleep -Milliseconds 750
    Start-Process -FilePath $CompanionExe -WorkingDirectory (Split-Path $CompanionExe)
    Write-Host 'MateMCP Agent started in the background; Companion opened for this install session.'
    Write-Host 'Companion will not open automatically on future sign-ins.'
}
