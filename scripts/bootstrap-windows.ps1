$ErrorActionPreference = 'Stop'

$repo = if ($env:MATEMCP_REPO) { $env:MATEMCP_REPO } else { 'vrassouli/MateMCP' }
$tag = if ($env:MATEMCP_DESKTOP_RELEASE_TAG) {
    $env:MATEMCP_DESKTOP_RELEASE_TAG
} elseif ($env:MATEMCP_AGENT_RELEASE_TAG) {
    $env:MATEMCP_AGENT_RELEASE_TAG
} else {
    'agent-latest'
}

$arch = $env:PROCESSOR_ARCHITECTURE
switch ($arch) {
    'ARM64' { $rid = 'win-arm64' }
    'AMD64' { $rid = 'win-x64' }
    'x86' { throw 'MateMCP does not support 32-bit Windows.' }
    default {
        if ([Environment]::Is64BitOperatingSystem) { $rid = 'win-x64' }
        else { throw "Unsupported Windows architecture: $arch" }
    }
}

# The native Companion is currently published for Windows x64. Keep the
# bootstrap compatible with Windows ARM64 by falling back to the Agent-only
# package until the Companion artifact is published for that RID as well.
$desktop = $rid -eq 'win-x64'
if ($desktop) {
    $asset = "MateMCP-Desktop-$rid.zip"
    $installerName = 'install-desktop-windows.ps1'
    $productName = 'MateMCP Desktop (Agent + Companion)'
} else {
    $asset = "MateMCP-$rid.zip"
    $installerName = 'install-windows.ps1'
    $productName = 'MateMCP Agent'
    Write-Warning 'The native MateMCP Companion is not published for Windows ARM64 yet. Installing the Agent-only package.'
}

$url = "https://github.com/$repo/releases/download/$tag/$asset"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("matemcp-install-" + [Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot $asset
$extractPath = Join-Path $tempRoot 'package'

New-Item -ItemType Directory -Force -Path $tempRoot, $extractPath | Out-Null

try {
    Write-Host "Downloading $productName ($rid) from $tag..."
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing

    Write-Host 'Extracting package...'
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

    $installer = Join-Path $extractPath $installerName
    if (-not (Test-Path $installer)) {
        throw "Downloaded package does not contain $installerName"
    }

    Write-Host "Installing/upgrading $productName..."
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer
    if ($LASTEXITCODE -ne 0) {
        throw "MateMCP installer exited with code $LASTEXITCODE"
    }

    # The installer runs in a child PowerShell process, so propagate its PATH
    # change into the current bootstrap process as well. This makes `matemcp`
    # immediately available when bootstrap itself was invoked in the caller's shell.
    $bin = Join-Path $env:LOCALAPPDATA 'MateMCP\bin'
    $processParts = @($env:Path -split ';' | Where-Object { $_ })
    if ($processParts -notcontains $bin) {
        $env:Path = (($processParts + $bin) -join ';').Trim(';')
    }

    Write-Host ''
    Write-Host "$productName installation complete."
    if ($desktop) {
        Write-Host 'The Agent and native Companion are running and will start automatically on future sign-ins.'
    } else {
        Write-Host 'The Agent is running and will start automatically on future sign-ins.'
    }
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
