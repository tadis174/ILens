#Requires -Version 5.1

# ILens installer.
# Run: irm https://raw.githubusercontent.com/tadis174/ILens/main/install.ps1 | iex
# Project: https://github.com/tadis174/ILens

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ILens'
$zipUrl     = 'https://github.com/tadis174/ILens/releases/latest/download/ILens-windows-x64.zip'
$tempZip    = Join-Path $env:TEMP ("ILens-install-" + [guid]::NewGuid() + ".zip")
$guideUrl   = 'https://tadis174.github.io/ILens/guide.html'

function Write-Step    { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Success { param([string]$Message) Write-Host $Message -ForegroundColor Green }

# 1. Download the release ZIP (one retry on network failure).
Write-Step "Downloading ILens from $zipUrl"
$maxAttempts = 2
$attempt = 0
$downloaded = $false
while (-not $downloaded -and $attempt -lt $maxAttempts) {
    $attempt++
    try {
        # Use TLS 1.2 explicitly on older PowerShell hosts; harmless elsewhere.
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing
        $downloaded = $true
    } catch {
        if ($attempt -lt $maxAttempts) {
            Write-Warning "Download failed (attempt $attempt): $($_.Exception.Message). Retrying in 2 s..."
            Start-Sleep -Seconds 2
        } else {
            throw "Could not download from $zipUrl after $maxAttempts attempts: $($_.Exception.Message)"
        }
    }
}

# 2. Wipe existing install (overwrite-in-place upgrade).
if (Test-Path $installDir) {
    Write-Step "Removing existing install at $installDir"
    try {
        Remove-Item -Path $installDir -Recurse -Force
    } catch {
        throw @"
Could not remove existing install at $installDir: $($_.Exception.Message)
A running 'ilens.exe' process may be holding files open. Close any MCP client
sessions that loaded ILens (Claude Code, etc.) and re-run the installer.
"@
    }
}

# 3. Extract the ZIP. Wrap so the temp file always gets cleaned up.
Write-Step "Extracting to $installDir"
try {
    New-Item -Path $installDir -ItemType Directory -Force | Out-Null
    Expand-Archive -Path $tempZip -DestinationPath $installDir -Force
} catch {
    throw "Could not extract $tempZip to $installDir: $($_.Exception.Message)"
} finally {
    if (Test-Path $tempZip) {
        Remove-Item -Path $tempZip -Force -ErrorAction SilentlyContinue
    }
}

# 4. Verify the binary landed. Missing here usually means antivirus quarantined it.
$binaryPath = Join-Path $installDir 'ILens.exe'
if (-not (Test-Path $binaryPath)) {
    throw @"
ILens.exe was not found at $binaryPath after extraction.
Antivirus software (Windows Defender, etc.) may have quarantined the file
mid-extract. Self-contained .NET binaries are sometimes flagged as suspicious
on first sight even when they aren't.

Try:
  1. Check your antivirus quarantine and restore ILens.exe.
  2. Add an exception for $installDir before re-running the installer.

See the troubleshooting section of the user guide: $guideUrl
"@
}

# 5. Run the binary to confirm it executes and capture the version.
Write-Step "Verifying binary"
$versionOutput = & $binaryPath --version 2>&1
if ($LASTEXITCODE -ne 0) {
    throw @"
$binaryPath exited with code $LASTEXITCODE when running --version.
Output: $versionOutput

The binary extracted but won't run. This is unusual; if you see Windows
SmartScreen or a "Windows protected your PC" dialog, allow the file and
re-run the installer.
"@
}
$version = ($versionOutput | Select-Object -First 1 | Out-String).Trim()

# 6. Add install dir to user-level PATH if not already present.
$pathChanged = $false
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($null -eq $userPath) { $userPath = '' }
$pathEntries = $userPath -split ';' | Where-Object { $_ -ne '' }
if ($pathEntries -notcontains $installDir) {
    Write-Step "Adding $installDir to user PATH"
    $newPath = if ($userPath -eq '') { $installDir } else { "$userPath;$installDir" }
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    $pathChanged = $true
} else {
    Write-Host "User PATH already contains $installDir; no change."
}

# 7. Success block.
Write-Host ''
Write-Success "Installed $version to $installDir"
if ($pathChanged) {
    Write-Success 'PATH updated for new shells.'
} else {
    Write-Success 'PATH already configured.'
}
Write-Host ''
Write-Host "To use 'ilens' in the current shell, run:"
Write-Host '  $env:PATH = "$env:LOCALAPPDATA\Programs\ILens;$env:PATH"'
Write-Host 'Or open a new terminal.'
Write-Host ''
Write-Host "See the user guide: $guideUrl"
