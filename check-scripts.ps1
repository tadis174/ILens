# Parse-check every tracked PowerShell script in the repo.
#
# A syntactically broken .ps1 (e.g. an install.ps1 with an unparseable `$var:`
# reference) fails to parse before it runs a single line — and nothing else in
# the build catches it: the end-to-end tests exercise the MCP server, and
# check.ps1 / check-staged.ps1 cover attribution. This gate runs as part of the
# /publish flow (the _ILensReleaseFlow target in Core/Core.csproj).
#
# Exit codes: 0 = all scripts parse, 1 = at least one failed to parse.

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$failed = $false

foreach ($rel in (& git -C $repoRoot ls-files '*.ps1')) {
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $repoRoot $rel), [ref]$null, [ref]$errors) | Out-Null
    if ($errors) {
        $failed = $true
        Write-Host "[FAIL] $rel"
        foreach ($e in $errors) {
            Write-Host "  line $($e.Extent.StartLineNumber): $($e.Message)"
        }
    }
}

if ($failed) { exit 1 }

Write-Host "[OK] All tracked PowerShell scripts parse."
exit 0
