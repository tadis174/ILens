# Post-stage allowlist check. Runs after `dotnet publish` has staged Build/dist/.
#
# Verifies that every file in the staged release matches an expected name —
# either the project's standard companion files, or a per-source attribution
# file derived from INDEX.md. Anything else fails: someone added a Copy line
# to the csproj publish target, dropped a new asset into Core/Assets/, or the IL
# trimmer left a residual without updating attribution.
#
# Exit codes: 0 = OK, 1 = unexpected file present (or expected file missing),
#             2 = setup issue (Build/dist/ not staged).

$ErrorActionPreference = 'Stop'

$repoRoot       = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$attributionDir = $PSScriptRoot
$indexPath      = Join-Path $attributionDir 'INDEX.md'
$allowlistPath  = Join-Path $attributionDir 'staged-allowlist.txt'
$distDir        = Join-Path $repoRoot 'Build\dist'
$tplDir         = Join-Path $distDir 'third-party-licenses'

if (-not (Test-Path $distDir)) {
    Write-Host "[SETUP] Build/dist/ not found. Run 'dotnet publish Core/Core.csproj -c Release' first."
    exit 2
}
if (-not (Test-Path $tplDir)) {
    Write-Host "[SETUP] Build/dist/third-party-licenses/ not found. Publish staging is incomplete."
    exit 2
}
if (-not (Test-Path $indexPath)) {
    Write-Host "[SETUP] third-party-licenses/INDEX.md not found."
    exit 2
}
if (-not (Test-Path $allowlistPath)) {
    Write-Host "[SETUP] third-party-licenses/staged-allowlist.txt not found."
    exit 2
}

# --- Allowlist for Build/dist/ root ---
# Read from staged-allowlist.txt (single source of truth shared with the
# csproj publish target's <Copy> commands). Skip blank lines and #-comments.
$expectedDistRoot = Get-Content $allowlistPath |
    ForEach-Object { $_.Trim() } |
    Where-Object { $_ -and ($_ -notmatch '^#') }

# --- Derive expected files for third-party-licenses/ from INDEX.md ---
# INDEX.md is the source of truth for which attribution filenames ship.
# Use OrdinalIgnoreCase so a case-difference between INDEX entries and the
# on-disk filenames doesn't produce asymmetric "unexpected" / "missing" reports.
$expectedTpl = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
[void]$expectedTpl.Add('INDEX.md')
foreach ($line in (Get-Content $indexPath)) {
    if ($line -match '^-\s+License:\s+`([^`]+)`') {
        [void]$expectedTpl.Add($matches[1])
    }
    elseif ($line -match '^-\s+Notice:\s+`([^`]+)`') {
        [void]$expectedTpl.Add($matches[1])
    }
}

# --- Inspect actual staged contents ---
$actualDistRoot = Get-ChildItem $distDir -File | Select-Object -ExpandProperty Name
$actualSubdirs  = Get-ChildItem $distDir -Directory | Select-Object -ExpandProperty Name
$actualTpl      = Get-ChildItem $tplDir -File | Select-Object -ExpandProperty Name
$actualTplDirs  = Get-ChildItem $tplDir -Directory | Select-Object -ExpandProperty Name

# --- Compare against allowlists ---
$unexpectedDistFiles = @($actualDistRoot | Where-Object { $_ -notin $expectedDistRoot })
$missingDistFiles    = @($expectedDistRoot | Where-Object { $_ -notin $actualDistRoot })
$unexpectedTplFiles  = @($actualTpl | Where-Object { -not $expectedTpl.Contains($_) })
$missingTplFiles     = @(($expectedTpl) | Where-Object { $_ -notin $actualTpl })

# Subdirectories: only third-party-licenses/ is allowed under dist/.
# Nothing is allowed under third-party-licenses/ as a subdir.
$unexpectedDistSubdirs = @($actualSubdirs | Where-Object { $_ -ne 'third-party-licenses' })
$unexpectedTplSubdirs  = @($actualTplDirs)

# --- Report ---
$failed = $false
function Report([string]$header, [string[]]$items) {
    if ($items.Count -eq 0) { return $false }
    Write-Host "[FAIL] $header"
    foreach ($i in $items) { Write-Host "  - $i" }
    return $true
}

if (Report "Unexpected files in Build/dist/ — not in allowlist:" $unexpectedDistFiles) { $failed = $true }
if (Report "Unexpected subdirectories in Build/dist/:" $unexpectedDistSubdirs) { $failed = $true }
if (Report "Missing expected files in Build/dist/:" $missingDistFiles) { $failed = $true }
if (Report "Unexpected files in Build/dist/third-party-licenses/ — not declared in INDEX.md:" $unexpectedTplFiles) { $failed = $true }
if (Report "Unexpected subdirectories in Build/dist/third-party-licenses/:" $unexpectedTplSubdirs) { $failed = $true }
if (Report "Missing files in Build/dist/third-party-licenses/ (declared in INDEX.md but not staged):" $missingTplFiles) { $failed = $true }

if ($failed) {
    Write-Host ""
    Write-Host "If a new file is intentional, add it to the allowlist in this script and (if it is third-party content) update INDEX.md and vendor the upstream LICENSE."
    exit 1
}

Write-Host "[OK] Staged output matches allowlist — $($actualDistRoot.Count) file(s) in Build/dist/, $($actualTpl.Count) file(s) in third-party-licenses/."
exit 0
