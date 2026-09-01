$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP'
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
