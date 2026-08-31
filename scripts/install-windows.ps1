param(
    [string]$Source = (Join-Path $PSScriptRoot 'payload'),
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP'
$Bin = Join-Path $Target 'bin'
$Exe = Join-Path $Target 'MateMCP.Agent.exe'
$Shim = Join-Path $Bin 'matemcp.cmd'

if (-not (Test-Path (Join-Path $Source 'MateMCP.Agent.exe'))) {
    throw "MateMCP payload not found at: $Source"
}

New-Item -ItemType Directory -Force -Path $Target, $Bin | Out-Null

Get-Process 'MateMCP.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Get-ChildItem $Target -Force | Where-Object { $_.Name -ne 'bin' } | Remove-Item -Recurse -Force
Copy-Item (Join-Path $Source '*') $Target -Recurse -Force

@"
@echo off
"$Exe" %*
"@ | Set-Content -Path $Shim -Encoding ASCII

$packageUninstall = Join-Path $PSScriptRoot 'uninstall-windows.ps1'
if (Test-Path $packageUninstall) {
    Copy-Item $packageUninstall (Join-Path $Target 'uninstall-windows.ps1') -Force
}

$currentUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$parts = @($currentUserPath -split ';' | Where-Object { $_ })
if ($parts -notcontains $Bin) {
    $newPath = (($parts + $Bin) -join ';').Trim(';')
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
}

$currentProcessParts = @($env:Path -split ';' | Where-Object { $_ })
if ($currentProcessParts -notcontains $Bin) {
    $env:Path = (($currentProcessParts + $Bin) -join ';').Trim(';')
}

Write-Host 'MateMCP installed/upgraded.'
Write-Host "Binary: $Exe"
Write-Host "Command: $Shim"
Write-Host "Config: $env:APPDATA\MateMCP\appsettings.json"
Write-Host "Credentials: Windows Credential Manager"
Write-Host "Uninstall: powershell -ExecutionPolicy Bypass -File `"$Target\uninstall-windows.ps1`""
Write-Host ''
Write-Host 'The matemcp command is now available in this installer process and in newly opened terminals.'

if (-not $NoStart) {
    Start-Process -FilePath $Exe -WorkingDirectory $Target
    Write-Host 'MateMCP Agent started.'
}
