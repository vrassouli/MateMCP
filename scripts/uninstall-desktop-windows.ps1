$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:LOCALAPPDATA 'MateMCP'
$CompanionUninstall = Join-Path $Root 'Companion\uninstall-companion-windows.ps1'
$AgentUninstall = Join-Path $Root 'uninstall-windows.ps1'

# Remove Companion first because Agent uninstall removes the MateMCP installation root.
if (Test-Path $CompanionUninstall) {
    & $CompanionUninstall
}

if (Test-Path $AgentUninstall) {
    & $AgentUninstall
} else {
    Get-Process 'MateMCP.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if (Test-Path $Root) { Remove-Item $Root -Recurse -Force }
}

Write-Host 'MateMCP Desktop removed.'
Write-Host 'Configuration and credentials were intentionally preserved by the component uninstallers.'
