param(
    [switch]$NoStart,
    [ValidateSet('Normal','Elevated')]
    [string]$AgentMode = 'Normal'
)

$ErrorActionPreference = 'Stop'

$AgentPayload = Join-Path $PSScriptRoot 'agent-payload'
$CompanionPayload = Join-Path $PSScriptRoot 'companion-payload'
$AgentInstaller = Join-Path $PSScriptRoot 'install-windows.ps1'
$CompanionInstaller = Join-Path $PSScriptRoot 'install-companion-windows.ps1'

if (-not (Test-Path $AgentInstaller)) { throw "Agent installer not found: $AgentInstaller" }
if (-not (Test-Path $CompanionInstaller)) { throw "Companion installer not found: $CompanionInstaller" }

if ($AgentMode -eq 'Elevated') {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"",'-AgentMode','Elevated')
        if ($NoStart) { $arguments += '-NoStart' }
        $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList ($arguments -join ' ') -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Elevated MateMCP Desktop installer exited with code $($process.ExitCode)." }
        return
    }
}

# Agent installation intentionally runs first because it refreshes %LOCALAPPDATA%\MateMCP.
# -AgentOnly prevents install-windows.ps1 from delegating back to this Desktop wrapper.
& $AgentInstaller -Source $AgentPayload -NoStart -AgentOnly -AgentMode $AgentMode
& $CompanionInstaller -Source $CompanionPayload -NoStart

$InstalledRoot = Join-Path $env:LOCALAPPDATA 'MateMCP'
Copy-Item (Join-Path $PSScriptRoot 'uninstall-desktop-windows.ps1') (Join-Path $InstalledRoot 'uninstall-desktop-windows.ps1') -Force

Write-Host ''
Write-Host 'MateMCP Desktop installed/upgraded.'
Write-Host 'Components: background Agent + on-demand Agent Companion'
Write-Host "Agent execution mode: $AgentMode"
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$InstalledRoot\uninstall-desktop-windows.ps1`""

if (-not $NoStart) {
    $ConfigureMode = Join-Path $InstalledRoot 'configure-agent-mode-windows.ps1'
    & $ConfigureMode -Mode $AgentMode
    $CompanionExe = Join-Path $InstalledRoot 'Companion\MateMCP.Agent.Companion.exe'
    Start-Sleep -Milliseconds 750
    Start-Process -FilePath $CompanionExe -WorkingDirectory (Split-Path $CompanionExe)
    Write-Host 'MateMCP Agent started in the configured background mode; Companion opened for this install session.'
    Write-Host 'Companion will not open automatically on future sign-ins.'
}
