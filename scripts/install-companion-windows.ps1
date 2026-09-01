param(
    [string]$Source = (Join-Path $PSScriptRoot 'payload'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP\Companion'
$Exe = Join-Path $Target 'MateMCP.Agent.Companion.exe'
$StartupDirectory = [Environment]::GetFolderPath('Startup')
$StartupShortcut = Join-Path $StartupDirectory 'MateMCP Agent Companion.lnk'
$ProgramsDirectory = [Environment]::GetFolderPath('Programs')
$ProgramsShortcut = Join-Path $ProgramsDirectory 'MateMCP Agent Companion.lnk'

if (-not (Test-Path (Join-Path $Source 'MateMCP.Agent.Companion.exe'))) {
    throw "MateMCP Agent Companion payload not found at: $Source"
}

Get-Process 'MateMCP.Agent.Companion' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-Item $StartupShortcut -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Target | Out-Null
Get-ChildItem $Target -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source '*') $Target -Recurse -Force

$packageUninstall = Join-Path $PSScriptRoot 'uninstall-companion-windows.ps1'
if (Test-Path $packageUninstall) {
    Copy-Item $packageUninstall (Join-Path $Target 'uninstall-companion-windows.ps1') -Force
}

$shortcutShell = New-Object -ComObject WScript.Shell
$shortcut = $shortcutShell.CreateShortcut($ProgramsShortcut)
$shortcut.TargetPath = $Exe
$shortcut.WorkingDirectory = $Target
$shortcut.Description = 'MateMCP Agent Companion'
$shortcut.Save()

Write-Host 'MateMCP Agent Companion installed/upgraded.'
Write-Host "Application: $Exe"
Write-Host "Start Menu shortcut: $ProgramsShortcut"
Write-Host 'Auto-start: disabled (open Companion only when needed)'
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$Target\uninstall-companion-windows.ps1`""

if (-not $NoStart) {
    Start-Process -FilePath $Exe -WorkingDirectory $Target
    Write-Host 'MateMCP Agent Companion opened.'
}
