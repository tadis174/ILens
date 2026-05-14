# ILens end-to-end test runner. Committed and Claude-skill-independent — invoke
# it by hand, from CI, or from the /publish gate; it does not depend on Claude.
#
# Spawns the real ILens MCP server and exercises its tools over the MCP protocol
# (the tests live in Tests/DecompilerAssemblyTests.cs).
#
# Modes:
#   (no args)          Inner loop — builds Core (Release) and tests that build
#                      output under Build/bin/Core/Release/.
#   -ILensExe <path>   Tests exactly that executable, with no build of its own.
#                      The /publish gate passes the staged single-file
#                      Build/dist/ILens.exe here.
#
# Exit codes: 0 = tests passed, 1 = a test failed, 2 = setup/build problem.

param(
    [string]$ILensExe
)

$ErrorActionPreference = 'Stop'
# dotnet build/test exit codes are inspected via $LASTEXITCODE and mapped to this
# script's own exit codes below — a non-zero native exit must not throw.
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testProject = Join-Path $PSScriptRoot 'Tests.csproj'

if (-not (Test-Path $testProject)) {
    Write-Host "[SETUP] Test project not found: $testProject"
    exit 2
}

if (-not $ILensExe) {
    # Inner-loop mode: build Core (Release), then test that build output.
    Write-Host "Building Core (Release)..."
    dotnet build (Join-Path $repoRoot 'Core' 'Core.csproj') -c Release -v q
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[SETUP] Core build failed."
        exit 2
    }
    $found = Get-ChildItem (Join-Path $repoRoot 'Build' 'bin' 'Core' 'Release') -Recurse -Filter 'ILens.exe' -ErrorAction SilentlyContinue
    $ILensExe = $found | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $ILensExe -or -not (Test-Path $ILensExe)) {
    Write-Host "[SETUP] ILens executable not found: $ILensExe"
    exit 2
}
$ILensExe = (Resolve-Path $ILensExe).Path

Write-Host "Testing against: $ILensExe"
$env:ILENS_E2E_EXE = $ILensExe
dotnet test $testProject --logger "console;verbosity=normal"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[FAIL] One or more end-to-end tests failed."
    exit 1
}

Write-Host "[OK] All end-to-end tests passed."
exit 0
