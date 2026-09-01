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
& $AgentInstaller -Source $AgentPayload -NoStart
& $CompanionInstaller -Source $CompanionPayload -NoStart

$InstalledRoot = Join-Path $env:LOCALAPPDATA 'MateMCP'
Copy-Item (Join-Path $PSScriptRoot 'uninstall-desktop-windows.ps1') (Join-Path $InstalledRoot 'uninstall-desktop-windows.ps1') -Force

Write-Host ''
Write-Host 'MateMCP Desktop installed/upgraded.'
Write-Host 'Components: Agent + Agent Companion'
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$InstalledRoot\uninstall-desktop-windows.ps1`""

if (-not $NoStart) {
    $AgentExe = Join-Path $InstalledRoot 'MateMCP.Agent.exe'
    $CompanionExe = Join-Path $InstalledRoot 'Companion\MateMCP.Agent.Companion.exe'
    Start-Process -FilePath $AgentExe -WorkingDirectory $InstalledRoot
    Start-Sleep -Milliseconds 750
    Start-Process -FilePath $CompanionExe -WorkingDirectory (Split-Path $CompanionExe)
    Write-Host 'MateMCP Agent and Companion started.'
}
