$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP\Companion'
$StartupDirectory = [Environment]::GetFolderPath('Startup')
$StartupShortcut = Join-Path $StartupDirectory 'MateMCP Agent Companion.lnk'

Get-Process 'MateMCP.Agent.Companion' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item $StartupShortcut -Force -ErrorAction SilentlyContinue

if (Test-Path $Target) {
    Remove-Item $Target -Recurse -Force
}

Write-Host 'MateMCP Agent Companion uninstalled.'
