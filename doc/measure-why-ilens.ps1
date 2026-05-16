# Measure the real token cost of inspecting a .NET assembly with ILens versus
# the "dump the whole file" baseline, and write the numbers to
# doc/why-ilens-stats.json. The /doc skill reads that artifact to build the
# user guide's "Why ILens" comparison table, so the table shows measured
# figures instead of estimates.
#
# Run this on demand, not on every /doc run. The artifact is committed; the
# numbers only need refreshing when the measured assembly
# (ICSharpCode.Decompiler.dll) is bumped or ILens's tool output changes.
#
# Modes:
#   (no args)          Inner loop: build Core (Release) and measure that build
#                      output under Build/bin/Core/Release/.
#   -ILensExe <path>   Measure exactly that executable, with no build.
#
# Requires ilspycmd on PATH (dotnet tool install --global ilspycmd) for the
# baseline. ilspycmd is the ICSharpCode decompiler CLI; ILens bundles the same
# engine, so the comparison reflects scope, not tool choice.
#
# Exit codes: 0 = artifact written, 2 = setup/build problem.

param(
    [string]$ILensExe
)

$ErrorActionPreference = 'Stop'
# dotnet build and ilspycmd exit codes are inspected via $LASTEXITCODE; a
# non-zero native exit must not throw before it can be mapped to an exit code.
$PSNativeCommandUseErrorActionPreference = $false

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outPath  = Join-Path $PSScriptRoot 'why-ilens-stats.json'

# --- Minimal MCP client over stdio ---------------------------------------
# ILens speaks MCP JSON-RPC 2.0 over stdio: one JSON object per line. The
# helpers below are just enough of a client to handshake and call read-only
# tools. stderr is left attached to this console (see the spawn block) so an
# unread pipe cannot deadlock the server.

function Send-McpMessage {
    param(
        [System.IO.StreamWriter]$Writer,
        [hashtable]$Message
    )
    $json = $Message | ConvertTo-Json -Depth 12 -Compress
    $Writer.Write($json + "`n")
    $Writer.Flush()
}

function Read-McpLine {
    param(
        [System.IO.StreamReader]$Reader,
        [int]$TimeoutMs = 60000
    )
    $task = $Reader.ReadLineAsync()
    if (-not $task.Wait($TimeoutMs)) {
        throw "Timed out after $TimeoutMs ms waiting for output from ILens."
    }
    return $task.Result
}

function Invoke-McpRequest {
    param(
        [System.IO.StreamWriter]$Writer,
        [System.IO.StreamReader]$Reader,
        [hashtable]$Request
    )
    Send-McpMessage -Writer $Writer -Message $Request
    while ($true) {
        $line = Read-McpLine -Reader $Reader
        if ($null -eq $line) {
            throw "ILens closed its output stream before answering request id=$($Request.id)."
        }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $obj = $line | ConvertFrom-Json
        if (($obj.PSObject.Properties.Name -contains 'id') -and ($obj.id -eq $Request.id)) {
            return $obj
        }
        # Anything else on stdout (e.g. a notification) is not our response.
    }
}

function Measure-ILensTool {
    param(
        [System.IO.StreamWriter]$Writer,
        [System.IO.StreamReader]$Reader,
        [int]$Id,
        [string]$ToolName,
        [hashtable]$Arguments
    )
    $request = @{
        jsonrpc = '2.0'
        id      = $Id
        method  = 'tools/call'
        params  = @{ name = $ToolName; arguments = $Arguments }
    }
    $response = Invoke-McpRequest -Writer $Writer -Reader $Reader -Request $request
    if ($response.PSObject.Properties.Name -contains 'error') {
        throw "ILens tool '$ToolName' failed: $($response.error | ConvertTo-Json -Compress)"
    }
    if ($response.result.isError) {
        $errText = ($response.result.content | ForEach-Object { $_.text }) -join "`n"
        throw "ILens tool '$ToolName' returned an error: $errText"
    }
    return ($response.result.content |
        Where-Object { $_.type -eq 'text' } |
        ForEach-Object { $_.text }) -join "`n"
}

function Get-TokenEstimate {
    param(
        [int]$Chars,
        [int]$Divisor
    )
    return [int][Math]::Ceiling($Chars / [double]$Divisor)
}

# --- Resolve ilspycmd (the baseline tool) --------------------------------

$ilspycmd = Get-Command ilspycmd -ErrorAction SilentlyContinue
if (-not $ilspycmd) {
    Write-Host "[SETUP] ilspycmd not found on PATH. Install it with:"
    Write-Host "    dotnet tool install --global ilspycmd"
    Write-Host "Then re-run this script. ilspycmd is the ICSharpCode decompiler CLI;"
    Write-Host "ILens bundles the same engine, so it gives an apples-to-apples baseline."
    exit 2
}

# --- Resolve the ILens executable ----------------------------------------

if (-not $ILensExe) {
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

# --- Resolve the target assembly -----------------------------------------
# ICSharpCode.Decompiler.dll ships next to ILens.exe in every Core build
# output. That directory is also the --allow-root handed to ILens, mirroring
# the end-to-end test fixture (Tests/ILensServerFixture.cs).

$allowRoot = Split-Path $ILensExe
$targetDll = Join-Path $allowRoot 'ICSharpCode.Decompiler.dll'
if (-not (Test-Path $targetDll)) {
    Write-Host "[SETUP] Target assembly not found next to ILens.exe: $targetDll"
    exit 2
}
$targetDll = (Resolve-Path $targetDll).Path

Write-Host "Measuring with ILens: $ILensExe"
Write-Host "Measuring target:     $targetDll"

# --- Measure the target assembly's shape (no ILens, no ilspycmd) ----------

$fileSizeBytes = (Get-Item $targetDll).Length
$targetVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($targetDll).FileVersion

try {
    $asm = [System.Reflection.Assembly]::LoadFrom($targetDll)
    $publicTypeCount = $asm.GetExportedTypes().Count
}
catch {
    # GetExportedTypes throws ReflectionTypeLoadException if a dependent type
    # cannot be resolved; count the types that did load. Unwrap any wrapper
    # exception PowerShell layered on top.
    $rtle = $_.Exception
    while ($rtle -and -not ($rtle -is [System.Reflection.ReflectionTypeLoadException])) {
        $rtle = $rtle.InnerException
    }
    if (-not $rtle) { throw }
    $publicTypeCount = ($rtle.Types | Where-Object { $_ -and $_.IsPublic }).Count
}

# --- Measure the baseline: a full ilspycmd dump of the assembly -----------

$ilspycmdVersionRaw = (& $ilspycmd --version) -join "`n"
if ($ilspycmdVersionRaw -match '\d+\.\d+\.\d+(\.\d+)?') {
    $ilspycmdVersion = $Matches[0]
}
else {
    $ilspycmdVersion = $ilspycmdVersionRaw.Trim()
}

Write-Host "Running ilspycmd (baseline: whole-assembly decompile)..."
$baselineText = (& $ilspycmd $targetDll) -join "`n"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[SETUP] ilspycmd exited with code $LASTEXITCODE."
    exit 2
}
$baselineChars = $baselineText.Length

# --- Measure ILens tool output sizes -------------------------------------

Write-Host "Spawning ILens and measuring tool output..."

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $ILensExe
$psi.ArgumentList.Add('--allow-root')
$psi.ArgumentList.Add($allowRoot)
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
# Leave stderr attached to this console: ILens logs there, and an unread
# redirected stderr pipe could fill and deadlock the process.
$psi.RedirectStandardError  = $false
$psi.UseShellExecute        = $false
$psi.StandardInputEncoding  = [System.Text.UTF8Encoding]::new($false)
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)

$proc = [System.Diagnostics.Process]::Start($psi)
try {
    $stdin  = $proc.StandardInput
    $stdout = $proc.StandardOutput

    # Handshake: initialize request, then the initialized notification.
    $initResponse = Invoke-McpRequest -Writer $stdin -Reader $stdout -Request @{
        jsonrpc = '2.0'
        id      = 1
        method  = 'initialize'
        params  = @{
            protocolVersion = '2024-11-05'
            capabilities    = @{}
            clientInfo      = @{ name = 'ilens-measure'; version = '1.0' }
        }
    }
    if ($initResponse.PSObject.Properties.Name -contains 'error') {
        throw "ILens rejected initialize: $($initResponse.error | ConvertTo-Json -Compress)"
    }
    Send-McpMessage -Writer $stdin -Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    $listTypesText = Measure-ILensTool -Writer $stdin -Reader $stdout -Id 2 -ToolName 'list_types' -Arguments @{
        assembly      = $targetDll
        namespaceName = 'ICSharpCode.Decompiler.TypeSystem'
    }
    $summarizeText = Measure-ILensTool -Writer $stdin -Reader $stdout -Id 3 -ToolName 'summarize_type' -Arguments @{
        assembly = $targetDll
        typeName = 'ICSharpCode.Decompiler.CSharp.CSharpDecompiler'
    }
    $findMethodsText = Measure-ILensTool -Writer $stdin -Reader $stdout -Id 4 -ToolName 'find_methods' -Arguments @{
        assembly    = $targetDll
        namePattern = 'Decompile'
    }
}
finally {
    if (-not $proc.HasExited) {
        try { $proc.StandardInput.Close() } catch { }
        if (-not $proc.WaitForExit(10000)) {
            $proc.Kill()
        }
    }
}

# --- Assemble and write the artifact -------------------------------------

$charsPerToken = 4

$ilensVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ILensExe).ProductVersion
if ([string]::IsNullOrWhiteSpace($ilensVersion)) {
    $ilensVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ILensExe).FileVersion
}
# ProductVersion carries a +<sourceRevisionId> suffix from the SDK; keep just
# the version itself (matches csproj <Version> and the guide's displayed version).
$ilensVersion = ($ilensVersion -split '\+')[0]

$result = [ordered]@{
    schemaVersion   = 1
    measuredAtUtc   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd')
    charsPerToken   = $charsPerToken
    ilensVersion    = $ilensVersion
    target          = [ordered]@{
        assembly        = [System.IO.Path]::GetFileName($targetDll)
        version         = $targetVersion
        fileSizeBytes   = $fileSizeBytes
        publicTypeCount = $publicTypeCount
    }
    ilspycmdVersion = $ilspycmdVersion
    baseline        = [ordered]@{
        label           = 'full ilspycmd dump'
        chars           = $baselineChars
        tokensEstimated = (Get-TokenEstimate -Chars $baselineChars -Divisor $charsPerToken)
    }
    rows            = @(
        [ordered]@{
            task            = 'List the public types in a namespace'
            ilensTool       = 'list_types'
            ilensCall       = "list_types(namespaceName: 'ICSharpCode.Decompiler.TypeSystem')"
            chars           = $listTypesText.Length
            tokensEstimated = (Get-TokenEstimate -Chars $listTypesText.Length -Divisor $charsPerToken)
        }
        [ordered]@{
            task            = 'See the API surface of one mid-sized class'
            ilensTool       = 'summarize_type'
            ilensCall       = "summarize_type(typeName: 'ICSharpCode.Decompiler.CSharp.CSharpDecompiler')"
            chars           = $summarizeText.Length
            tokensEstimated = (Get-TokenEstimate -Chars $summarizeText.Length -Divisor $charsPerToken)
        }
        [ordered]@{
            task            = 'Find which types expose a given method'
            ilensTool       = 'find_methods'
            ilensCall       = "find_methods(namePattern: 'Decompile')"
            chars           = $findMethodsText.Length
            tokensEstimated = (Get-TokenEstimate -Chars $findMethodsText.Length -Divisor $charsPerToken)
        }
    )
}

$result | ConvertTo-Json -Depth 10 | Set-Content -Path $outPath -Encoding utf8

# --- Summary -------------------------------------------------------------

Write-Host ""
Write-Host "=== Why-ILens measurements ==="
Write-Host ("Target:   {0} {1} ({2:N0} bytes, {3} public types)" -f `
    $result.target.assembly, $result.target.version, $result.target.fileSizeBytes, $result.target.publicTypeCount)
Write-Host ("Baseline: {0,10:N0} chars  ~{1,8:N0} tokens  (full ilspycmd dump)" -f `
    $result.baseline.chars, $result.baseline.tokensEstimated)
foreach ($row in $result.rows) {
    Write-Host ("  {0,-15} {1,9:N0} chars  ~{2,8:N0} tokens  ({3})" -f `
        $row.ilensTool, $row.chars, $row.tokensEstimated, $row.task)
}
Write-Host ""
Write-Host "Artifact written: $outPath"

if ($ilspycmdVersion -ne $targetVersion) {
    Write-Warning ("ilspycmd version ($ilspycmdVersion) differs from the measured assembly " +
        "($targetVersion). The comparison is cleanest when they match.")
}

exit 0
