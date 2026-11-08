# Lists what an update of the libraries has to look at: the package references
# of the tracked projects that have a newer version on NuGet, and the places in
# Core that rest on what a library version does. Run it after each release; it
# needs PowerShell 7, the .NET SDK and network access, nothing more.
#
# Packages: each package reference of a tracked .csproj or .vbproj whose package
# has a version above the one referenced, the newest stable one, or for a
# reference to a prerelease the newest prerelease. A reference with a <Hold>
# element in its project file is listed as held, with the element's text as the
# reason why it stays.
#
# Library behavior: every remark under Core/ that says "Library behavior, read
# in <package> <version>:" names what the code rests on, the package and the
# version it was last read or measured in, and the case to measure it on. Each
# is listed with its file and line; one read in another version than Core
# references is marked to be measured again. A workaround whose behavior is gone
# goes, and a remark whose behavior changed is brought in line.
#
# Exit codes: 0 = nothing to decide, 1 = a newer version that no hold covers or
# a remark to measure again, 2 = an error, such as a failed restore.

$ErrorActionPreference = 'Stop'
trap {
    Write-Host "[ERROR] $_"
    exit 2
}

$repoRoot = $PSScriptRoot
$toDecide = $false

function Read-Outdated([string]$project, [switch]$Prerelease) {
    $arguments = @('list', $project, 'package', '--outdated', '--format', 'json')
    if ($Prerelease) { $arguments += '--include-prerelease' }
    $json = & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet list package failed for ${project}: $json" }
    foreach ($framework in ($json | ConvertFrom-Json).projects.frameworks) {
        foreach ($package in $framework.topLevelPackages) { $package }
    }
}

Write-Host 'Packages:'
$listed = 0
foreach ($rel in (& git -C $repoRoot ls-files '*.csproj' '*.vbproj')) {
    $path = Join-Path $repoRoot $rel
    & dotnet restore $path --verbosity quiet | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed for $rel" }

    $references = ([xml](Get-Content -Raw $path)).SelectNodes('//PackageReference')
    $stable = @{}
    foreach ($package in (Read-Outdated $path)) { $stable[$package.id] = $package.latestVersion }
    # Only a reference to a prerelease is compared with prereleases, so only a project that
    # holds one is asked for them.
    $prerelease = @{}
    if (@($references | Where-Object { $_.Version -like '*-*' }).Count -gt 0) {
        foreach ($package in (Read-Outdated $path -Prerelease)) { $prerelease[$package.id] = $package.latestVersion }
    }

    foreach ($reference in ($references | Sort-Object Include)) {
        $requested = $reference.Version
        $latest = if ($requested -like '*-*') { $prerelease[$reference.Include] } else { $stable[$reference.Include] }
        if (-not $latest -or $latest -eq $requested) { continue }
        $listed++
        $hold = $reference.SelectSingleNode('Hold')
        if ($hold) {
            Write-Host "  $rel`: $($reference.Include) $requested, $latest available, held: $(($hold.InnerText -replace '\s+', ' ').Trim())"
        }
        else {
            Write-Host "  $rel`: $($reference.Include) $requested, $latest available"
            $toDecide = $true
        }
    }
}
if ($listed -eq 0) { Write-Host '  Every reference is at the newest version.' }

Write-Host 'Library behavior:'
$referenced = @{}
foreach ($reference in ([xml](Get-Content -Raw (Join-Path $repoRoot 'Core/Core.csproj'))).SelectNodes('//PackageReference')) {
    $referenced[$reference.Include] = $reference.Version
}
$marker = 'Library behavior, read in (?<package>[A-Za-z][\w.]*) (?<version>\d[\w.-]*):'
$remarks = Get-ChildItem (Join-Path $repoRoot 'Core') -Recurse -Filter '*.cs' |
    Select-String -Pattern $marker
foreach ($match in $remarks) {
    $package = $match.Matches[0].Groups['package'].Value
    $version = $match.Matches[0].Groups['version'].Value
    $where = "$([System.IO.Path]::GetRelativePath($repoRoot, $match.Path).Replace('\', '/')):$($match.LineNumber)"
    if (-not $referenced.ContainsKey($package)) {
        Write-Host "  $where`: $package $version, not a package Core references"
    }
    elseif ($referenced[$package] -ne $version) {
        Write-Host "  $where`: $package $version, measure again on $($referenced[$package])"
        $toDecide = $true
    }
    else {
        Write-Host "  $where`: $package $version"
    }
}
if (-not $remarks) { Write-Host '  None marked.' }

if ($toDecide) { exit 1 }
exit 0
