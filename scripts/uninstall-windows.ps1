param(
    [switch]$AgentOnly
)

$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP'
$DesktopUninstall = Join-Path $Target 'uninstall-desktop-windows.ps1'
$CompanionDir = Join-Path $Target 'Companion'

# If this is a Desktop installation, the obvious uninstall-windows.ps1 entry
# point removes both components. The Desktop wrapper passes -AgentOnly when it
# intentionally reaches the Agent component cleanup.
if (-not $AgentOnly -and (Test-Path $DesktopUninstall) -and (Test-Path $CompanionDir)) {
    & $DesktopUninstall
    return
}

$Bin = Join-Path $Target 'bin'
$StartupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'MateMCP Agent.lnk'

Get-Process 'MateMCP.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item $StartupShortcut -Force -ErrorAction SilentlyContinue

$currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($currentUserPath) {
    $newPath = (($currentUserPath -split ';' | Where-Object { $_ -and $_ -ne $Bin }) -join ';').Trim(';')
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
}

if (Test-Path $Target) {
    Remove-Item $Target -Recurse -Force
}

Write-Host 'MateMCP Agent removed.'
Write-Host 'Configuration under %APPDATA%\MateMCP and credentials in Windows Credential Manager were intentionally preserved.'
