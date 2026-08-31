$ErrorActionPreference = 'Stop'

$repo = if ($env:MATEMCP_REPO) { $env:MATEMCP_REPO } else { 'vrassouli/MateMCP' }
$tag = if ($env:MATEMCP_AGENT_RELEASE_TAG) { $env:MATEMCP_AGENT_RELEASE_TAG } else { 'agent-latest' }

$arch = $env:PROCESSOR_ARCHITECTURE
switch ($arch) {
    'ARM64' { $rid = 'win-arm64' }
    'AMD64' { $rid = 'win-x64' }
    'x86' { throw 'MateMCP Agent does not support 32-bit Windows.' }
    default {
        if ([Environment]::Is64BitOperatingSystem) { $rid = 'win-x64' }
        else { throw "Unsupported Windows architecture: $arch" }
    }
}

$asset = "MateMCP-$rid.zip"
$url = "https://github.com/$repo/releases/download/$tag/$asset"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("matemcp-install-" + [Guid]::NewGuid().ToString('N'))
$zipPath = Join-Path $tempRoot $asset
$extractPath = Join-Path $tempRoot 'package'

New-Item -ItemType Directory -Force -Path $tempRoot, $extractPath | Out-Null

try {
    Write-Host "Downloading MateMCP Agent ($rid) from $tag..."
    Invoke-WebRequest -Uri $url -OutFile $zipPath -UseBasicParsing

    Write-Host 'Extracting package...'
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

    $installer = Join-Path $extractPath 'install-windows.ps1'
    if (-not (Test-Path $installer)) {
        throw "Downloaded package does not contain install-windows.ps1"
    }

    Write-Host 'Installing/upgrading MateMCP Agent...'
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
}
finally {
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
