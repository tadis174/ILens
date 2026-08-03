# ILens field-test installer. Committed and Claude-skill-independent — invoke it
# by hand or from the /publish gate; it does not depend on Claude.
#
# Replaces the machine-wide winget installation of ILens with a build of the
# current working tree, so every MCP client on this box exercises the code being
# developed instead of the last published release. Without this, the ilens tools
# always run the last shipped binary and cannot see local changes at all.
#
# Modes:
#   (no args)   Build the working tree and install it over the winget binary.
#   -Verify     Build the working tree and report whether the installed binary
#               already matches it. Touches no repository or install state — this
#               is the /publish gate.
#   -Restore    Put the backed-up genuine release binary back.
#
# Verify works by comparison rather than by a recorded marker because a Release
# publish is byte-reproducible: two publishes of identical source produce an
# identical single-file exe. So a hash match proves the installed binary was
# built from exactly this source. It also means a docs-only or tests-only change
# does not invalidate a field test, because it cannot change the shipped artifact.
#
# Exit codes: 0 = success (for -Verify: installed binary matches the working tree)
#             1 = installed binary does not match the working tree
#             2 = setup problem (build failed, no winget install, nothing to restore)

param(
    [switch]$Verify,
    [switch]$Restore
)

$ErrorActionPreference = 'Stop'
# dotnet exit codes are inspected via $LASTEXITCODE and mapped to this script's
# own exit codes below — a non-zero native exit must not throw.
$PSNativeCommandUseErrorActionPreference = $false

if ($Verify -and $Restore) {
    Write-Host "[SETUP] -Verify and -Restore are mutually exclusive."
    exit 2
}

$repoRoot = $PSScriptRoot
$workDir  = Join-Path $repoRoot 'Build' 'field-test'

# The backup lives outside the repository on purpose. Under Build/ it would be
# build output, and a clean would delete it — silently, while a field-test binary
# was still installed. The next field test would then find no backup, save *that*
# binary as the published one, and -Restore would later hand back a dev build
# while reporting it had restored the release.
$backupExe = Join-Path $env:LOCALAPPDATA 'ILens' 'field-test-backup' 'ILens.exe'

# The winget portable install puts the real binary in a package directory and
# exposes it through a symlink on PATH. Overwrite the package binary, not the
# symlink — replacing the link would break the alias winget maintains.
function Get-InstalledExe {
    $pattern = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages\Tadis.ILens*\ILens.exe'
    return @(Get-ChildItem $pattern -ErrorAction SilentlyContinue)
}

# A running ILens holds an exclusive lock on its own image and on the Release
# build output it was started from, so both the build and the overwrite need it
# gone. Every ILens process on this box runs the binary being replaced, so
# stopping all of them is correct rather than over-broad.
function Stop-RunningILens {
    $running = @(Get-Process ILens -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host "Stopping $($running.Count) running ILens process(es)..."
        $running | Stop-Process -Force
    }
}

function Build-WorkingTree([string]$destination) {
    Stop-RunningILens

    if (Test-Path $destination) {
        Remove-Item $destination -Recurse -Force
    }

    Write-Host "Building the working tree (Release, single-file)..."
    # _ILensInner=true suppresses _ILensReleaseFlow. That target enforces a clean
    # tree, runs the release staging, bumps <Version>, and writes a release
    # commit — none of which may happen during field testing.
    #
    # Out-Host rather than a bare call: a function returns everything left on its
    # success stream, and MSBuild writes errors and warnings to stdout even at
    # -v q. In the pipeline they would be returned alongside the path, so a failed
    # build would come back as a non-empty array and sail straight through the
    # caller's null check.
    dotnet publish (Join-Path $repoRoot 'Core' 'Core.csproj') `
        -c Release -p:_ILensInner=true -p:PublishDir="$destination\" -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[SETUP] Build failed."
        return $null
    }

    $built = Join-Path $destination 'ILens.exe'
    if (-not (Test-Path $built)) {
        Write-Host "[SETUP] Build produced no ILens.exe at $built."
        return $null
    }
    return $built
}

if ($Restore) {
    if (-not (Test-Path $backupExe)) {
        Write-Host "[SETUP] No backup at $backupExe — nothing to restore."
        Write-Host "        Reinstall the published binary with: winget install --force Tadis.ILens"
        exit 2
    }
    $installed = Get-InstalledExe
    if ($installed.Count -ne 1) {
        Write-Host "[SETUP] Expected exactly one winget install of Tadis.ILens, found $($installed.Count)."
        exit 2
    }

    Stop-RunningILens

    Copy-Item $backupExe $installed[0].FullName -Force
    Remove-Item $backupExe -Force
    Write-Host "[OK] Restored the published binary: $(& $installed[0].FullName --version)"
    exit 0
}

$installed = Get-InstalledExe
if ($installed.Count -gt 1) {
    Write-Host "[SETUP] Expected at most one winget install of Tadis.ILens, found $($installed.Count):"
    $installed | ForEach-Object { Write-Host "        $($_.FullName)" }
    exit 2
}

if ($Verify) {
    if ($installed.Count -eq 0) {
        Write-Host "ILens is not installed via winget, so the working tree cannot have been field tested."
        Write-Host "Install it with 'winget install Tadis.ILens', then run this script with no arguments."
        exit 1
    }

    $built = Build-WorkingTree (Join-Path $workDir 'verify')
    if (-not $built) { exit 2 }

    $builtHash     = (Get-FileHash $built -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash $installed[0].FullName -Algorithm SHA256).Hash

    if ($builtHash -eq $installedHash) {
        Write-Host "[OK] The installed binary was built from the current working tree."
        exit 0
    }

    Write-Host "The installed binary does not match the current working tree."
    Write-Host "  installed: $installedHash"
    Write-Host "  built now: $builtHash"
    Write-Host "  installed version: $(& $installed[0].FullName --version)"
    exit 1
}

# Install mode.
if ($installed.Count -eq 0) {
    Write-Host "[SETUP] ILens is not installed via winget, so there is nothing to replace."
    Write-Host "        Install it first: winget install Tadis.ILens"
    exit 2
}

$target = $installed[0].FullName
$built  = Build-WorkingTree (Join-Path $workDir 'staging')
if (-not $built) { exit 2 }

# Back up the genuine release binary once, so -Restore has something to put back.
# Only on the first field test — a second one would otherwise overwrite the
# backup with the previous dev build.
if (-not (Test-Path $backupExe)) {
    New-Item -ItemType Directory -Force (Split-Path $backupExe) | Out-Null
    Copy-Item $target $backupExe
    Write-Host "Backed up the published binary ($(& $target --version)) for -Restore."
}

Copy-Item $built $target -Force
Write-Host "[OK] Field-test build installed over the winget binary."
Write-Host "     $target"
Write-Host "     now reports: $(& $target --version)"
Write-Host ""
Write-Host "This is machine-wide: every MCP client on this box now runs the working-tree"
Write-Host "build. Restore the published binary with: field-test.ps1 -Restore"
exit 0
