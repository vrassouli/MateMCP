param(
    [string]$Source = (Join-Path $PSScriptRoot 'payload'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP\Companion'
$Exe = Join-Path $Target 'MateMCP.Agent.Companion.exe'
$WebViewUserData = Join-Path $Target 'MateMCP.Agent.Companion.exe.WebView2'
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
# WebView2 keeps its user-data/cache beside the executable. That directory is
# runtime state, not install payload, and child WebView2 processes may still
# hold cache files briefly after the Companion exits. Preserve it during
# upgrades so a locked cache file cannot abort the whole Desktop update.
Get-ChildItem $Target -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $WebViewUserData } |
    Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source '*') $Target -Recurse -Force

$packageUninstall = Join-Path $PSScriptRoot 'uninstall-companion-windows.ps1'
if (Test-Path $packageUninstall) {
    Copy-Item $packageUninstall (Join-Path $Target 'uninstall-companion-windows.ps1') -Force
}

# The unified install can run elevated when the Agent uses Elevated mode. The
# Companion/WebView2 subtree must still be writable by the normal desktop user.
& icacls.exe $Target /setintegritylevel '(OI)(CI)M' /T /C | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restore Medium integrity on the MateMCP Companion installation.' }

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
