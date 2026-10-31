# Asks an ILens server one tool call, or a series of them, over MCP and prints
# the answers as the server gave them. For checking a change or measuring on the
# working tree's build without running the whole suite and without installing
# the build.
#
# Without -ILensExe it builds Core (Release) first and asks that build output,
# so the answer comes from the working tree as it stands. It builds no test
# fixture: those under Build/bin/Tests/Debug/net10.0 are as Tests/run-test.ps1
# last built them. Relative paths, in -AllowRoot and in a call's arguments, are
# taken from the current directory.
#
# Modes (exactly one):
#   -Tool <name> [-Arguments <hashtable or JSON object>]
#                      One call. Prints the answer text and nothing else.
#   -CallsFile <path>  A JSON array of calls, [{ "name": ..., "arguments": {...} }, ...],
#                      asked of one server process, in order. Each answer follows
#                      a line naming its call and how long it took.
#   -ToolList          No call. Prints the size of each tool in the tool list a
#                      client receives, and the total, counted as
#                      Tests/ToolSchemaTests.cs counts toward its ceiling: name,
#                      description and raw input schema.
#   -ToolParameters    No call. Prints each tool's parameters as the tool list
#                      declares them: a parameter's schema without its description,
#                      and whether the tool requires it.
#
# Options:
#   -AllowRoot <dir>[,<dir>...]
#                      The directories the server may read assemblies from.
#   -ServerArgument <arg>[,<arg>...]
#                      Further startup arguments for the server, each option and each
#                      value an entry of its own: -ServerArgument '--max-total-size', '64'.
#   -Memory            After each answer, a line with the server's private bytes,
#                      the figure its memory budget is measured on.
#   -ServerLog <path>  Writes the server's stderr there, which names every
#                      assembly it loads and closes. Dropped otherwise.
#   -TimeoutSeconds <n>
#                      Gives up when an answer takes longer, and ends the server.
#                      Without it the script waits as long as the server takes.
#   -ILensExe <path>   Asks exactly that executable, with no build.
#
# Example:
#   Tests/ask-server.ps1 -AllowRoot Build/bin/Tests/Debug/net10.0 -Tool find_types `
#       -Arguments @{ assembly = 'Build/bin/Tests/Debug/net10.0/CallShapes.dll' }
#
# Exit codes: 0 = every call was answered, 1 = a tool answered with an error,
# 2 = anything else: usage, build, a server that did not start, stopped answering
# or took longer than -TimeoutSeconds, or an error of this script.

param(
    [Parameter(Mandatory)][string[]]$AllowRoot,
    [string[]]$ServerArgument,
    [string]$Tool,
    $Arguments,
    [string]$CallsFile,
    [switch]$ToolList,
    [switch]$ToolParameters,
    [switch]$Memory,
    [string]$ServerLog,
    [int]$TimeoutSeconds,
    [string]$ILensExe
)

$ErrorActionPreference = 'Stop'
# The dotnet build exit code is inspected via $LASTEXITCODE and mapped to this
# script's own exit codes below — a non-zero native exit must not throw.
$PSNativeCommandUseErrorActionPreference = $false
# An error nothing below handles ends the script with 2. PowerShell's own exit
# code for it is 1, which a caller would read as a tool's error answer.
trap {
    Write-Host "[SETUP] $($_.Exception.Message)"
    exit 2
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

if (@($Tool, $CallsFile, $ToolList.IsPresent, $ToolParameters.IsPresent | Where-Object { $_ }).Count -ne 1) {
    Write-Host "[SETUP] Give exactly one of -Tool, -CallsFile, -ToolList and -ToolParameters."
    exit 2
}

$calls = @()
if ($Tool) {
    if ($Arguments -is [string]) { $Arguments = $Arguments | ConvertFrom-Json -AsHashtable }
    $calls = @(@{ name = $Tool; arguments = $Arguments })
}
elseif ($CallsFile) {
    if (-not (Test-Path $CallsFile)) {
        Write-Host "[SETUP] Calls file not found: $CallsFile"
        exit 2
    }
    $calls = @(Get-Content $CallsFile -Raw | ConvertFrom-Json -AsHashtable)
}

if (-not $ILensExe) {
    # Everything the build prints goes to the host, so that this script's output
    # stays the answers alone.
    Write-Host "Building Core (Release)..."
    dotnet build (Join-Path $repoRoot 'Core' 'Core.csproj') -c Release -v q | Out-Host
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

$psi = [System.Diagnostics.ProcessStartInfo]::new($ILensExe)
foreach ($root in $AllowRoot) {
    $psi.ArgumentList.Add('--allow-root')
    $psi.ArgumentList.Add($root)
}
foreach ($argument in $ServerArgument) {
    $psi.ArgumentList.Add($argument)
}
$psi.WorkingDirectory       = $PWD.ProviderPath
$psi.RedirectStandardInput  = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError  = $true
$psi.UseShellExecute        = $false
$psi.StandardInputEncoding  = [System.Text.UTF8Encoding]::new($false)
$psi.StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)

$proc = [System.Diagnostics.Process]::Start($psi)
# Read stderr as it comes: the server logs there, and a pipe nobody reads fills
# up and blocks it.
$stderr = $proc.StandardError.ReadToEndAsync()

# ILens speaks MCP JSON-RPC 2.0 over stdio: one JSON object per line.
function Send-Message([hashtable]$message) {
    $proc.StandardInput.Write(($message | ConvertTo-Json -Depth 20 -Compress) + "`n")
    $proc.StandardInput.Flush()
}

# Reads up to the response to request $id and returns it as a JsonElement, so
# that an answer's text and a schema's raw text arrive exactly as sent. What
# else the server writes, such as a notification, is passed over.
function Receive-Response([int]$id) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ($true) {
        $read = $proc.StandardOutput.ReadLineAsync()
        if ($TimeoutSeconds -gt 0) {
            $left = $deadline - [DateTime]::UtcNow
            if ($left -le [TimeSpan]::Zero -or -not $read.Wait($left)) {
                throw "The server did not answer request $id within $TimeoutSeconds seconds."
            }
        }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { throw "The server closed its output before answering request $id." }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $message = [System.Text.Json.JsonDocument]::Parse($line).RootElement
        $messageId = [System.Text.Json.JsonElement]::new()
        if ($message.TryGetProperty('id', [ref]$messageId) -and
            $messageId.ValueKind -eq [System.Text.Json.JsonValueKind]::Number -and
            $messageId.GetInt32() -eq $id) {
            return $message
        }
    }
}

function Get-RpcError([System.Text.Json.JsonElement]$response) {
    $rpcError = [System.Text.Json.JsonElement]::new()
    if (-not $response.TryGetProperty('error', [ref]$rpcError)) { return $null }
    return "MCP error $($rpcError.GetProperty('code').GetInt32()): $($rpcError.GetProperty('message').GetString())"
}

$toolFailed = $false
$problem = $null
try {
    Send-Message @{ jsonrpc = '2.0'; id = 1; method = 'initialize'; params = @{
        protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'ask-server'; version = '1' } } }
    $rejected = Get-RpcError (Receive-Response 1)
    if ($rejected) { throw "The server rejected initialize. $rejected" }
    Send-Message @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    if ($ToolList) {
        Send-Message @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
        $total = 0
        foreach ($entry in (Receive-Response 2).GetProperty('result').GetProperty('tools').EnumerateArray()) {
            $name = $entry.GetProperty('name').GetString()
            # A tool without a description counts 0 for it, as in the test.
            $description = [System.Text.Json.JsonElement]::new()
            $descriptionLength = 0
            if ($entry.TryGetProperty('description', [ref]$description)) {
                $descriptionLength = ([string]$description.GetString()).Length
            }
            $size = $name.Length + $descriptionLength + $entry.GetProperty('inputSchema').GetRawText().Length
            $total += $size
            '{0,-28} {1,6}  (description {2})' -f $name, $size, $descriptionLength
        }
        "total $total"
    }

    if ($ToolParameters) {
        Send-Message @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
        foreach ($entry in (Receive-Response 2).GetProperty('result').GetProperty('tools').EnumerateArray()) {
            $entry.GetProperty('name').GetString()
            $schema = $entry.GetProperty('inputSchema')
            $required = @()
            $requiredNames = [System.Text.Json.JsonElement]::new()
            if ($schema.TryGetProperty('required', [ref]$requiredNames)) {
                $required = @($requiredNames.EnumerateArray() | ForEach-Object { $_.GetString() })
            }
            # A tool without parameters has no properties at all.
            $properties = [System.Text.Json.JsonElement]::new()
            if (-not $schema.TryGetProperty('properties', [ref]$properties)) { continue }
            foreach ($parameter in $properties.EnumerateObject()) {
                $declared = @($parameter.Value.EnumerateObject() |
                    Where-Object { $_.Name -ne 'description' } |
                    ForEach-Object { "$($_.Name)=$($_.Value.GetRawText())" }) -join '  '
                if ($parameter.Name -in $required) { $declared += '  required' }
                '  {0,-28} {1}' -f $parameter.Name, $declared
            }
        }
    }

    $id = 2
    foreach ($call in $calls) {
        $callArguments = $call['arguments'] ?? @{}
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        Send-Message @{ jsonrpc = '2.0'; id = $id; method = 'tools/call'; params = @{ name = $call['name']; arguments = $callArguments } }
        $response = Receive-Response $id
        $watch.Stop()

        if ($CallsFile) {
            "===== $($call['name']) $($callArguments | ConvertTo-Json -Depth 20 -Compress) ($($watch.ElapsedMilliseconds) ms)"
        }
        $rpcError = Get-RpcError $response
        if ($rpcError) {
            $toolFailed = $true
            $rpcError
        }
        else {
            $result = $response.GetProperty('result')
            $isError = [System.Text.Json.JsonElement]::new()
            if ($result.TryGetProperty('isError', [ref]$isError) -and
                $isError.ValueKind -eq [System.Text.Json.JsonValueKind]::True) {
                $toolFailed = $true
            }
            ($result.GetProperty('content').EnumerateArray() |
                Where-Object { $_.GetProperty('type').GetString() -eq 'text' } |
                ForEach-Object { $_.GetProperty('text').GetString() }) -join "`n"
        }
        if ($Memory) {
            $proc.Refresh()
            $megabytes = ($proc.PrivateMemorySize64 / 1MB).ToString('F1', [cultureinfo]::InvariantCulture)
            "===== private bytes: $megabytes MB"
        }
        $id++
    }
}
catch {
    $problem = $_.Exception.Message
}
finally {
    try { $proc.StandardInput.Close() } catch { }
    if (-not $proc.WaitForExit(10000)) { $proc.Kill($true) }
}

$log = $stderr.Result
if ($ServerLog) { Set-Content -Path $ServerLog -Value $log -Encoding utf8 -NoNewline }

if ($problem) {
    Write-Host "[FAIL] $problem"
    if ($log) { Write-Host $log.TrimEnd() }
    exit 2
}
if ($toolFailed) { exit 1 }
exit 0
