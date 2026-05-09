# Attribution gate. Runs as part of /publish pre-flight (Step 1).
#
# Verifies that vendored third-party attribution under Source/Attribution/
# matches the build's actual dependencies and supporting files. Format
# conventions for INDEX.md and per-source READMEs are documented in
# Source/Attribution/README.md.
#
# Checks:
#   1. Completeness            — every package in project.assets.json is in INDEX
#   2. No stale entries        — every explicit INDEX entry is still in the build
#   3. LICENSE files present   — every source's vendored file exists on disk
#   4. Apache NOTICE accountability  — Apache sources have NOTICE or documented N/A
#   5. Runtime version match   — vendored runtime notices match the bundled .NET version
#   6. Upstream drift          — vendored files match their upstream URLs (network)
#   7. NuGet metadata          — every package's nuspec projectUrl maps to an INDEX source
#
# Flags:
#   -Offline   skip Check 6 (no network calls)
#
# Exit codes: 0 = OK, 1 = attribution drift, 2 = setup issue (no project.assets.json).

param(
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'

$attributionDir = $PSScriptRoot
$repoRoot       = (Resolve-Path (Join-Path $attributionDir '..\..')).Path
$indexPath      = Join-Path $attributionDir 'INDEX.md'
$assetsPath     = Join-Path $repoRoot 'Build\obj\project.assets.json'

if (-not (Test-Path $assetsPath)) {
    Write-Host "[SETUP] Build/obj/project.assets.json not found. Run 'dotnet restore' first."
    exit 2
}
if (-not (Test-Path $indexPath)) {
    Write-Host "[SETUP] Source/Attribution/INDEX.md not found."
    exit 2
}

# --- Utilities ---

function Normalize-Url([string]$url) {
    if (-not $url) { return $null }
    return ($url.Trim().ToLowerInvariant() -replace '^http:', 'https:' -replace '/+$', '')
}

function Normalize-Text([string]$text) {
    # Strip BOM, normalize line endings to \n. Trailing-newline differences are kept.
    if (-not $text) { return '' }
    return ($text -replace "`r`n", "`n" -replace "^\xEF\xBB\xBF", '')
}

function Get-VendoredLocalFilename([string]$url) {
    $last = ($url -split '/')[-1]
    switch -regex ($last) {
        '^LICENSE(\.[Tt][Xx][Tt]|\.md)?$'              { return 'LICENSE' }
        '^NOTICE(\.[Tt][Xx][Tt]|\.md)?$'               { return 'NOTICE' }
        '^THIRD-PARTY-NOTICES\.[Tt][Xx][Tt]$'          { return 'THIRD-PARTY-NOTICES.txt' }
        default                                        { return $last }
    }
}

# --- Parse INDEX.md ---
# State machine: '## ' starts a source; subsequent '- License:' / '- Notice:' /
# '- Source:' / '- Packages:' (with indented sub-bullets) populate the current source.

$sources       = @{}
$currentName   = $null
$inPackages    = $false

foreach ($line in (Get-Content $indexPath)) {
    if ($line -match '^##\s+(.+?)\s*$') {
        $currentName = $matches[1]
        $sources[$currentName] = [ordered]@{
            License    = $null
            Notice     = $null
            SourceUrls = New-Object System.Collections.ArrayList
            Packages   = New-Object System.Collections.ArrayList
        }
        $inPackages = $false
    }
    elseif ($currentName) {
        if ($line -match '^-\s+License:\s+`([^`]+)`') {
            $sources[$currentName].License = $matches[1]
            $inPackages = $false
        }
        elseif ($line -match '^-\s+Notice:\s+`([^`]+)`') {
            $sources[$currentName].Notice = $matches[1]
            $inPackages = $false
        }
        elseif ($line -match '^-\s+Source:\s+(.+?)\s*$') {
            $urls = $matches[1] -split '\s*,\s*'
            foreach ($u in $urls) {
                [void]$sources[$currentName].SourceUrls.Add($u)
            }
            $inPackages = $false
        }
        elseif ($line -match '^-\s+Packages:') {
            $inPackages = $true
        }
        elseif ($inPackages -and $line -match '^\s{2,}-\s+(\S+)') {
            [void]$sources[$currentName].Packages.Add($matches[1])
        }
        elseif ($line -match '^-\s+') {
            $inPackages = $false
        }
    }
}

# --- Read packages from project.assets.json ---
# Iterate targets[<tfm>] so we can filter build-only deps. A package with no
# 'compile' or 'runtime' entries (e.g. Microsoft.NET.ILLink.Tasks, which is the
# IL trimmer — props/tools only, no runtime DLLs) does not ship in the binary
# and is intentionally excluded from attribution.
$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
$pkgsInBuild = @()
$targetsBlock = $null
foreach ($t in $assets.targets.PSObject.Properties) {
    $targetsBlock = $t.Value  # take the first/only TFM target
    break
}
if ($null -eq $targetsBlock) {
    Write-Host "[SETUP] project.assets.json has no targets — run 'dotnet restore' first."
    exit 2
}
foreach ($p in $targetsBlock.PSObject.Properties) {
    $entry = $p.Value
    if ($entry.type -ne 'package') { continue }
    $hasCompile = ($entry.PSObject.Properties.Name -contains 'compile')
    $hasRuntime = ($entry.PSObject.Properties.Name -contains 'runtime')
    if (-not ($hasCompile -or $hasRuntime)) { continue }
    $name, $ver = $p.Name -split '/', 2
    $pkgsInBuild += [pscustomobject]@{ Name = $name; Version = $ver }
}

# --- NuGet cache: packageFolders + libraries-by-key for nuspec lookup ---
# project.assets.json's packageFolders lists every directory the resolver
# pulls from (user `.nuget`, the VS Shared cache, the SDK fallback, or a
# custom location set via $env:NUGET_PACKAGES / NuGet.Config). Each library
# entry has a `path` relative to one of these folders and a `files` list
# that includes the nuspec. Searching all folders makes the gate work
# regardless of where packages actually landed.
$packageFolders = @()
foreach ($f in $assets.packageFolders.PSObject.Properties) {
    $packageFolders += $f.Name.TrimEnd('\', '/')
}
$librariesByKey = @{}
foreach ($l in $assets.libraries.PSObject.Properties) {
    $librariesByKey[$l.Name] = $l.Value
}

# --- Build the union of indexed package patterns ---
$indexedPatterns = @()
foreach ($s in $sources.Values) { $indexedPatterns += @($s.Packages) }

function Test-PackageMatchesAnyPattern([string]$name, [string[]]$patterns) {
    foreach ($p in $patterns) {
        if ($p -eq $name) { return $true }
        if ($p.EndsWith('*')) {
            $prefix = $p.Substring(0, $p.Length - 1)
            if ($name.StartsWith($prefix)) { return $true }
        }
    }
    return $false
}

# --- Build allowlist of normalized projectUrls ---
$allowedProjectUrls = @{}
foreach ($srcName in $sources.Keys) {
    foreach ($u in $sources[$srcName].SourceUrls) {
        $norm = Normalize-Url $u
        if ($norm) { $allowedProjectUrls[$norm] = $srcName }
    }
}

# === Check 1: Completeness ===
$missingFromIndex = @()
foreach ($p in $pkgsInBuild) {
    if (-not (Test-PackageMatchesAnyPattern $p.Name $indexedPatterns)) {
        $missingFromIndex += $p.Name
    }
}

# === Check 2: No stale entries ===
$staleInIndex = @()
foreach ($pat in $indexedPatterns) {
    if ($pat.EndsWith('*')) { continue }
    $found = $false
    foreach ($p in $pkgsInBuild) { if ($p.Name -eq $pat) { $found = $true; break } }
    if (-not $found) { $staleInIndex += $pat }
}

# === Check 3: LICENSE files present ===
$missingLicenseFiles = @()
foreach ($srcName in $sources.Keys) {
    $s = $sources[$srcName]
    if (-not $s.License) {
        $missingLicenseFiles += "Source '$srcName' has no License: line in INDEX.md"
        continue
    }
    if ($s.License -match '^(.+?)-(LICENSE|NOTICE|THIRD-PARTY-NOTICES\.txt)$') {
        $sourceDir  = $matches[1]
        $sourceFile = $matches[2]
        $expected   = Join-Path $attributionDir "$sourceDir\$sourceFile"
        if (-not (Test-Path $expected)) {
            $missingLicenseFiles += "INDEX names ``$($s.License)`` for source '$srcName' but $expected does not exist"
        }
    }
    else {
        $missingLicenseFiles += "License filename '$($s.License)' for source '$srcName' does not match expected pattern <source>-LICENSE / <source>-NOTICE / <source>-THIRD-PARTY-NOTICES.txt"
    }
}

# === Check 4: Apache NOTICE accountability ===
$apacheIssues = @()
foreach ($srcName in $sources.Keys) {
    $s = $sources[$srcName]
    $isApache = ($srcName -match 'Apache') -or ($s.License -and $s.License -match 'Apache')
    if (-not $isApache -and $s.License -and $s.License -match '^(.+?)-(LICENSE|NOTICE)$') {
        $licPath = Join-Path $attributionDir "$($matches[1])\$($matches[2])"
        if (Test-Path $licPath) {
            $head = (Get-Content $licPath -TotalCount 50) -join "`n"
            if ($head -match 'Apache License') { $isApache = $true }
        }
    }
    if (-not $isApache) { continue }
    if ($s.License -notmatch '^(.+?)-(LICENSE|NOTICE)$') { continue }
    $sourceDir  = $matches[1]
    $noticeFile = Join-Path $attributionDir "$sourceDir\NOTICE"
    $readmeFile = Join-Path $attributionDir "$sourceDir\README.md"
    if ((Test-Path $noticeFile) -or (Test-Path $readmeFile)) { continue }
    $apacheIssues += "Source '$srcName' is Apache-licensed but has no NOTICE file and no README.md documenting absence (Apache-2.0 §4(d))"
}

# === Check 5: Runtime version match ===
$runtimeIssues  = @()
$runtimeReadme  = Join-Path $attributionDir 'dotnet-runtime\README.md'
if (-not (Test-Path $runtimeReadme)) {
    $runtimeIssues += "Source/Attribution/dotnet-runtime/README.md missing — cannot verify runtime version"
}
else {
    $readmeText = Get-Content $runtimeReadme -Raw
    if ($readmeText -match 'Runtime version:\s*([\d]+\.[\d]+\.[\d]+)') {
        $vendoredVersion = $matches[1]
        $major = $vendoredVersion.Split('.')[0]
        $majorPattern = [regex]::Escape($major)
        try {
            $runtimes = & dotnet --list-runtimes 2>$null
            $installedMatch = $runtimes |
                Where-Object { $_ -match "^Microsoft\.NETCore\.App\s+$majorPattern\." } |
                ForEach-Object { ($_ -split '\s+')[1] } |
                Sort-Object { [Version]$_ } |
                Select-Object -Last 1
            if (-not $installedMatch) {
                $runtimeIssues += "No Microsoft.NETCore.App $major.x runtime installed — cannot verify version match"
            }
            elseif ($installedMatch -ne $vendoredVersion) {
                $runtimeIssues += "Runtime notices vendored for $vendoredVersion but installed runtime is $installedMatch — refresh dotnet-runtime/THIRD-PARTY-NOTICES.txt and update dotnet-runtime/README.md"
            }
        }
        catch {
            $runtimeIssues += "Failed to query dotnet runtimes: $($_.Exception.Message)"
        }
    }
    else {
        $runtimeIssues += "Could not parse 'Runtime version: X.Y.Z' from $runtimeReadme"
    }
}

# === Check 6: Upstream drift detection ===
# For each per-source README, parse 'Source URL:' lines, fetch each, compare
# against the corresponding vendored file.
$driftIssues = @()
$driftFetchCount = 0
if (-not $Offline) {
    foreach ($srcDir in (Get-ChildItem $attributionDir -Directory)) {
        $readme = Join-Path $srcDir.FullName 'README.md'
        if (-not (Test-Path $readme)) { continue }
        $urls = @()
        foreach ($line in (Get-Content $readme)) {
            if ($line -match '^-\s+Source URL:\s*(\S+)\s*$') {
                $urls += $matches[1]
            }
        }
        if ($urls.Count -eq 0) {
            $driftIssues += "$($srcDir.Name): README.md has no '- Source URL: <url>' line — cannot drift-check"
            continue
        }
        foreach ($url in $urls) {
            $localName = Get-VendoredLocalFilename $url
            $localPath = Join-Path $srcDir.FullName $localName
            if (-not (Test-Path $localPath)) {
                $driftIssues += "$($srcDir.Name): README declares Source URL $url which maps to local file $localName, but $localPath does not exist"
                continue
            }
            try {
                $resp = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30 -ErrorAction Stop
                $upstreamText = $resp.Content
                if ($upstreamText -is [byte[]]) {
                    $upstreamText = [System.Text.Encoding]::UTF8.GetString($upstreamText)
                }
            }
            catch {
                $driftIssues += "$($srcDir.Name): failed to fetch $url — $($_.Exception.Message)"
                continue
            }
            $vendoredText = Get-Content $localPath -Raw
            if ((Normalize-Text $upstreamText) -ne (Normalize-Text $vendoredText)) {
                $driftIssues += "$($srcDir.Name): vendored ``$localName`` differs from upstream $url — refresh the file (and bump the version in README if the upstream version changed)"
            }
            $driftFetchCount++
        }
    }
}

# === Check 7: NuGet metadata cross-check ===
$nugetIssues = @()
$nugetPackagesScanned = 0
foreach ($p in $pkgsInBuild) {
    $ver    = $p.Version
    $libKey = "$($p.Name)/$ver"
    $libEntry = $librariesByKey[$libKey]
    if ($null -eq $libEntry) {
        $nugetIssues += "$($p.Name)@${ver}: no entry in project.assets.json libraries section — run 'dotnet restore'"
        continue
    }
    $nuspecRel = $libEntry.files | Where-Object { $_ -match '\.nuspec$' } | Select-Object -First 1
    if (-not $nuspecRel) {
        $nugetIssues += "$($p.Name)@${ver}: no .nuspec listed in library files"
        continue
    }
    $nuspec = $null
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path $folder (Join-Path $libEntry.path $nuspecRel)
        if (Test-Path $candidate) { $nuspec = $candidate; break }
    }
    if (-not $nuspec) {
        $nugetIssues += "$($p.Name)@${ver}: nuspec not found in any of: $($packageFolders -join ', ')"
        continue
    }
    try {
        [xml]$xml = Get-Content $nuspec
    }
    catch {
        $nugetIssues += "$($p.Name)@${ver}: failed to parse nuspec — $($_.Exception.Message)"
        continue
    }
    $projectUrl = $xml.package.metadata.projectUrl
    if (-not $projectUrl) {
        # No projectUrl declared; skip silently. The package is still mapped to a source via INDEX.md package patterns.
        $nugetPackagesScanned++
        continue
    }
    $norm = Normalize-Url $projectUrl
    if (-not $allowedProjectUrls.ContainsKey($norm)) {
        $nugetIssues += "$($p.Name)@${ver}: nuspec projectUrl '$projectUrl' (normalized '$norm') is not allowlisted in any INDEX.md '- Source:' line. Either add it to the appropriate source, or move the package to its real source."
    }
    $nugetPackagesScanned++
}

# === Report ===
$failed = $false
function Report([string]$header, [string[]]$items) {
    if ($items.Count -eq 0) { return $false }
    Write-Host "[FAIL] $header"
    foreach ($i in $items) { Write-Host "  - $i" }
    return $true
}

if (Report "Completeness — $($missingFromIndex.Count) package(s) in build but not in INDEX.md:" $missingFromIndex) { $failed = $true }
if (Report "Stale entries — $($staleInIndex.Count) entry/entries in INDEX.md no longer in build:" $staleInIndex) { $failed = $true }
if (Report "LICENSE files:" $missingLicenseFiles) { $failed = $true }
if (Report "Apache NOTICE accountability:" $apacheIssues) { $failed = $true }
if (Report "Runtime version match:" $runtimeIssues) { $failed = $true }
if (Report "Upstream drift:" $driftIssues) { $failed = $true }
if (Report "NuGet metadata cross-check:" $nugetIssues) { $failed = $true }

if ($failed) { exit 1 }

$msg = "[OK] Attribution complete and current — $($pkgsInBuild.Count) packages mapped to $($sources.Count) sources, $nugetPackagesScanned nuspec(s) cross-checked"
if ($Offline) { $msg += ", drift check skipped (-Offline)" }
else { $msg += ", $driftFetchCount upstream URL(s) verified" }
$msg += "."
Write-Host $msg
exit 0
