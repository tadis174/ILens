#Requires -Version 5.1

# ILens uninstaller (script-installed copies only).
# Run: irm https://raw.githubusercontent.com/tadis174/ILens/main/uninstall.ps1 | iex
# Project: https://github.com/tadis174/ILens
#
# For winget-installed copies, run: winget uninstall Tadis.ILens

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ILens'

function Write-Step   { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Success { param([string]$Message) Write-Host $Message -ForegroundColor Green }

$didSomething = $false

# 1. Remove the install directory.
if (Test-Path $installDir) {
    Write-Step "Removing $installDir"
    try {
        Remove-Item -Path $installDir -Recurse -Force
    } catch {
        throw @"
Could not remove $installDir: $($_.Exception.Message)
A running 'ilens.exe' process may be holding files open. Close any MCP client
sessions that loaded ILens (Claude Code, etc.) and re-run the uninstaller.
"@
    }
    $didSomething = $true
} else {
    Write-Host "No install directory at $installDir; nothing to remove."
}

# 2. Strip the install dir from user-level PATH.
$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($null -eq $userPath) { $userPath = '' }
$pathEntries = $userPath -split ';' | Where-Object { $_ -ne '' }
if ($pathEntries -contains $installDir) {
    Write-Step "Removing $installDir from user PATH"
    $newEntries = $pathEntries | Where-Object { $_ -ne $installDir }
    $newPath = ($newEntries -join ';')
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
    $didSomething = $true
} else {
    Write-Host "User PATH does not contain $installDir; no change."
}

# 3. Confirmation.
Write-Host ''
if ($didSomething) {
    Write-Success 'ILens uninstalled.'
} else {
    Write-Success 'ILens was not installed (script path); nothing to do.'
}
Write-Host 'For winget-installed copies, run: winget uninstall Tadis.ILens'
