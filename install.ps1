#Requires -Version 5.1

# ILens installer.
# Run: irm https://raw.githubusercontent.com/tadis174/ILens/main/install.ps1 | iex
# Project: https://github.com/tadis174/ILens

$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:LOCALAPPDATA 'Programs\ILens'
$zipUrl     = 'https://github.com/tadis174/ILens/releases/latest/download/ILens-windows-x64.zip'
$tempZip    = Join-Path $env:TEMP ("ILens-install-" + [guid]::NewGuid() + ".zip")
$guideUrl   = 'https://tadis174.github.io/ILens/guide.html'
$issuesUrl  = 'https://github.com/tadis174/ILens/issues'
$troubleshootingUrl = "${guideUrl}#appendix-install-troubleshooting"

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

# 2. Extract the ZIP into a folder beside the install, so that the installed version stays
#    in place until the new one has shown that it starts. The folders an interrupted run
#    left beside the install are removed first, and the temp file always gets cleaned up.
$stagingDir  = "$installDir.new"
$previousDir = "$installDir.old"
foreach ($leftoverDir in $stagingDir, $previousDir) {
    if (Test-Path -LiteralPath $leftoverDir) {
        try {
            Remove-Item -LiteralPath $leftoverDir -Recurse -Force
        } catch {
            throw @"
Could not remove ${leftoverDir}, which an earlier run of the installer left: $($_.Exception.Message)
A file in that directory is still in use. Close any process holding it open
(file explorer window, editor, antivirus scan) and re-run.
"@
        }
    }
}
Write-Step "Extracting to $stagingDir"
try {
    New-Item -Path $stagingDir -ItemType Directory -Force | Out-Null
    Expand-Archive -Path $tempZip -DestinationPath $stagingDir -Force
} catch {
    throw "Could not extract $tempZip to ${stagingDir}: $($_.Exception.Message)"
} finally {
    if (Test-Path $tempZip) {
        Remove-Item -Path $tempZip -Force -ErrorAction SilentlyContinue
    }
}

# Steps 3 and 4 check the new version where it was extracted. A failure removes it and
# leaves the install folder as it was.
$stopped = "The installation stopped, and $installDir is as it was before."
try {
    # 3. Verify the binary landed. Missing here usually means antivirus quarantined it.
    $binaryPath = Join-Path $stagingDir 'ILens.exe'
    if (-not (Test-Path $binaryPath)) {
        throw @"
ILens.exe was not found in $stagingDir after extraction.
Antivirus software (Windows Defender, etc.) may have quarantined the file
mid-extract. Self-contained .NET binaries are sometimes flagged as suspicious
on first sight even when they aren't.

Try:
  1. Check your antivirus quarantine and allow ILens.exe there.
  2. Add an exception for $installDir and $stagingDir before re-running the installer.

$stopped
See the user guide's installation troubleshooting: $troubleshootingUrl
"@
    }

    # 4. Run the new binary to confirm it executes and capture the version. It is started
    #    without the shell, so that a start Windows refuses reports its Win32 error code:
    #    the call operator of PowerShell 7.7 replaces that code with an error of its own.
    Write-Step "Verifying binary"
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($binaryPath, '--version')
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    try {
        $process = [System.Diagnostics.Process]::Start($startInfo)
    } catch {
        $code = $null
        for ($inner = $_.Exception; $inner; $inner = $inner.InnerException) {
            if ($inner -is [System.ComponentModel.Win32Exception]) { $code = $inner.NativeErrorCode; break }
        }
        # Windows' own text for the code, with the file's name for its placeholder, as in
        # "%1 is not a valid Win32 application". The exception's message, which .NET under
        # PowerShell 7 begins with the path and working directory, serves only without a code.
        $reason = $_.Exception.Message
        if ($null -ne $code) {
            $reason = [System.ComponentModel.Win32Exception]::new($code).Message -replace '%1', 'ILens.exe'
        }
        # 4551 and 4556-4559: an App Control policy blocked the file. On a PC no organization
        # manages, that is most likely Smart App Control, which takes no exception for a
        # single program.
        if ($code -in 4551, 4556, 4557, 4558, 4559) {
            throw @"
Windows refused to start the new ILens.exe:
$reason

On a Windows 11 PC that no organization manages, this is most likely Smart App
Control. It blocks a program that neither carries a trusted signature nor has a
good reputation with Microsoft's cloud service, and ILens.exe is not signed.
Smart App Control has no exception for a single program, so ILens cannot run on
this PC while it is on. On a PC an organization manages, its App Control policy
blocked the file, and its administrator can allow it.

$stopped
The user guide's installation troubleshooting explains the block and the options:
  $troubleshootingUrl
Please report the block, so that the project learns how many users run into it:
  $issuesUrl
"@
        }
        throw @"
Windows refused to start the new ILens.exe:
$reason

$stopped
See the user guide's installation troubleshooting:
  $troubleshootingUrl
"@
    }
    $errorRead = $process.StandardError.ReadToEndAsync()
    $versionOutput = $process.StandardOutput.ReadToEnd()
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $output = ($versionOutput + $errorRead.Result).Trim()
    $process.Dispose()
    if ($exitCode -ne 0) {
        if (-not $output) { $output = '(none)' }
        throw @"
The new ILens.exe exited with code $exitCode when running --version.
Output: $output

$stopped
See the user guide's installation troubleshooting:
  $troubleshootingUrl
If it does not help, please report the output:
  $issuesUrl
"@
    }
    $version = ($versionOutput -split '\r?\n')[0].Trim()
} catch {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
    throw
}

# 5. Stop any running ILens processes, now that the new version has started, so that the
#    AI clients start the new one and the earlier one can be removed: Windows lets the
#    folder of a running program be renamed, but refuses to delete its file while any
#    instance is loaded. The typical holders are MCP server processes spawned by AI
#    clients (Claude Code sessions, Claude Desktop) that registered ILens. A process that
#    cannot be stopped shows up in step 6, where the earlier version cannot be removed.
if (Test-Path $installDir) {
    $running = @(Get-Process ILens -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Step "Stopping $($running.Count) running ILens process(es)"
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

# 6. Replace the install: move it aside, move the new version into its place, and remove
#    the earlier one. Renaming a folder fails as a whole while a file in it is held open,
#    so a failure here leaves the installed version complete.
$movedAside = $false
if (Test-Path $installDir) {
    Write-Step "Replacing the install at $installDir"
    try {
        Rename-Item -LiteralPath $installDir -NewName (Split-Path $previousDir -Leaf)
        $movedAside = $true
    } catch {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        throw @"
Could not move the existing install at $installDir aside: $($_.Exception.Message)
A file in that directory is still in use. Close any process holding it open
(file explorer window, editor, antivirus scan, MCP client session) and re-run.
$stopped
"@
    }
}
try {
    Rename-Item -LiteralPath $stagingDir -NewName (Split-Path $installDir -Leaf)
} catch {
    $failure = $_.Exception.Message
    $restored = -not $movedAside
    if ($movedAside) {
        try {
            Rename-Item -LiteralPath $previousDir -NewName (Split-Path $installDir -Leaf)
            $restored = $true
        } catch { }
    }
    if ($restored) {
        Remove-Item -LiteralPath $stagingDir -Recurse -Force -ErrorAction SilentlyContinue
        throw @"
Could not move the new version from $stagingDir to ${installDir}: $failure
$stopped
See the user guide's installation troubleshooting: $troubleshootingUrl
"@
    }
    throw @"
Could not move the new version from $stagingDir to ${installDir}: $failure
Moving the earlier version back failed as well: it is in $previousDir, and the new
one in $stagingDir. Rename either folder to $(Split-Path $installDir -Leaf) to use it.
See the user guide's installation troubleshooting: $troubleshootingUrl
"@
}
if ($movedAside) {
    try {
        Remove-Item -LiteralPath $previousDir -Recurse -Force
    } catch {
        Write-Warning "Could not remove the earlier version at ${previousDir}: $($_.Exception.Message) Delete it once nothing uses it."
    }
}

# 7. Add install dir to user-level PATH if not already present.
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

# 8. Success block.
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
