$ErrorActionPreference = 'Stop'

$Target = Join-Path $env:LOCALAPPDATA 'MateMCP\Companion'
$StartupDirectory = [Environment]::GetFolderPath('Startup')
$StartupShortcut = Join-Path $StartupDirectory 'MateMCP Agent Companion.lnk'
$ProgramsDirectory = [Environment]::GetFolderPath('Programs')
$ProgramsShortcut = Join-Path $ProgramsDirectory 'MateMCP Agent Companion.lnk'

# Stop every Companion process and wait for Windows to release the self-contained
# runtime files before removing the installation directory. Stop-Process can
# return slightly before mapped DLLs (for example clrjit.dll) are fully released.
$processes = @(Get-Process 'MateMCP.Agent.Companion' -ErrorAction SilentlyContinue)
foreach ($process in $processes) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
}
foreach ($process in $processes) {
    try { Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue }
    catch { }
}

Remove-Item $StartupShortcut -Force -ErrorAction SilentlyContinue
Remove-Item $ProgramsShortcut -Force -ErrorAction SilentlyContinue

if (Test-Path $Target) {
    $removed = $false
    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Remove-Item $Target -Recurse -Force -ErrorAction Stop
            $removed = $true
            break
        }
        catch {
            if ($attempt -eq 20) { throw }
            Start-Sleep -Milliseconds 250
        }
    }

    if (-not $removed -and (Test-Path $Target)) {
        throw "Failed to remove MateMCP Agent Companion installation directory: $Target"
    }
}

Write-Host 'MateMCP Agent Companion uninstalled.'
