param(
    [ValidateSet('Normal','Elevated')]
    [string]$Mode,
    [switch]$NoStart,
    [string]$AgentRoot = (Join-Path $env:LOCALAPPDATA 'MateMCP')
)

$ErrorActionPreference = 'Stop'

$ConfigRoot = Join-Path $env:APPDATA 'MateMCP'
$ModeFile = Join-Path $ConfigRoot 'agent-run-mode.txt'
$StartupDirectory = [Environment]::GetFolderPath('Startup')
$StartupShortcut = Join-Path $StartupDirectory 'MateMCP Agent.lnk'
$HiddenLauncher = Join-Path $AgentRoot 'start-agent-hidden.vbs'
$TaskName = 'MateMCP Agent'
$WScript = Join-Path $env:WINDIR 'System32\wscript.exe'

if (-not (Test-Path $HiddenLauncher)) { throw "MateMCP Agent launcher not found: $HiddenLauncher" }

New-Item -ItemType Directory -Force -Path $ConfigRoot | Out-Null

# Stop whichever startup mechanism is currently active before changing it.
Get-Process 'MateMCP.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
try { Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue } catch { }
try { Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }
Remove-Item $StartupShortcut -Force -ErrorAction SilentlyContinue

if ($Mode -eq 'Elevated') {
    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object System.Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Changing MateMCP Agent to Elevated mode requires an Administrator-authorized PowerShell process.'
    }

    $userId = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $WScript -Argument "`"$HiddenLauncher`"" -WorkingDirectory $AgentRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $userId
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $userId -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Force | Out-Null
}
else {
    $shortcutShell = New-Object -ComObject WScript.Shell
    $shortcut = $shortcutShell.CreateShortcut($StartupShortcut)
    $shortcut.TargetPath = $WScript
    $shortcut.Arguments = "`"$HiddenLauncher`""
    $shortcut.WorkingDirectory = $AgentRoot
    $shortcut.WindowStyle = 7
    $shortcut.Description = 'MateMCP Agent (background)'
    $shortcut.Save()
}

Set-Content -Path $ModeFile -Value $Mode -Encoding ASCII

if (-not $NoStart) {
    if ($Mode -eq 'Elevated') {
        Start-ScheduledTask -TaskName $TaskName
    }
    else {
        Start-Process -FilePath $WScript -ArgumentList "`"$HiddenLauncher`""
    }
}

Write-Host "MateMCP Agent execution mode: $Mode"
Write-Host "Mode state: $ModeFile"
