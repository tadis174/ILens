# Asks two ILens builds the same questions about one or more assemblies and
# reports every answer that differs. For seeing what a change to the server
# changes in its answers beyond what the suite pins: the build before the change
# against the build after it.
#
# Each build is asked through Tests/ask-server.ps1, by several servers side by
# side. Relative paths are taken from the current directory.
#
# The build before a change: copy Build/bin/Core/Release/net10.0/win-x64 somewhere
# before changing code and give its ILens.exe as -Old, or give the installed
# ilens.exe by its full path.
#
# Modes (exactly one):
#   -Tool <name> [-Arguments <hashtable or JSON object>] -Over Types|Methods
#                      The tool is asked once for each type of each assembly, or
#                      once for each method of each type. The script adds assembly
#                      and type_name to the arguments given, and for a method
#                      member_name and the parameter types as find_methods prints
#                      them, or a parameter count of 0. Where the tool takes a
#                      limit and none is given, the script sets one that cuts
#                      nothing, since a cut list hides the differences below the
#                      cut. Types and methods are listed by the new build, with
#                      accessibility all and without what the compiler generated.
#   -CallsFile <path>  A JSON array of calls, [{ "name": ..., "arguments": {...} }, ...],
#                      asked of both builds as it is. Needs -AllowRoot.
#
# Options:
#   -Old <path>        The executable of the build to compare against.
#   -New <path>        The executable of the build to check. Without it the script
#                      builds Core (Release) and takes that build output.
#   -Assembly <path>[,<path>...]
#                      The assemblies to ask about, with -Tool. Each one's
#                      directory is an allow-root.
#   -AllowRoot <dir>[,<dir>...]
#                      Further directories the servers may read assemblies from.
#   -Step <n>          With -Tool, every nth type only.
#   -Chunks <n>        The servers side by side for each build. Default 2.
#   -OutputDirectory <dir>
#                      Where the calls, the answers and the report go. Default: a
#                      new folder under Build/compare.
#   -TimeoutSeconds <n>
#                      Gives up when one answer takes longer. Default 900.
#
# Output: for each assembly a line with the calls asked, the answers that are
# alike, how many of those are the same error on both builds, the answers that
# differ and the call time summed for each build. An assembly with nothing to ask
# about, one that lists no type or, with -Over Methods, none of whose types lists a
# method, gets a line saying so in place of the counts, and one in the report; it
# does not change the exit code. The report, report.txt in the output directory,
# holds each differing answer: the call, the first line of both answers, and the
# lines only one build gave.
#
# Example:
#   Tests/compare-builds.ps1 -Old C:\keep\before\ILens.exe `
#       -Assembly Build/bin/Tests/Debug/net10.0/CallShapes.dll `
#       -Tool analyze -Arguments @{ kind = 'used_by' } -Over Types
#
# Exit codes: 0 = no answer differs, 1 = at least one answer differs, 2 = anything
# else: usage, build, a server that failed, a type whose methods could not be
# listed, or an answer that is missing.

param(
    [Parameter(Mandatory)][string]$Old,
    [string]$New,
    [string[]]$Assembly,
    [string[]]$AllowRoot,
    [string]$Tool,
    $Arguments,
    [ValidateSet('Types', 'Methods')][string]$Over,
    [string]$CallsFile,
    [int]$Step = 1,
    [int]$Chunks = 2,
    [string]$OutputDirectory,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
# Exit codes of dotnet and of ask-server.ps1 are inspected below — a non-zero
# native exit must not throw.
$PSNativeCommandUseErrorActionPreference = $false
# An error nothing below handles ends the script with 2. PowerShell's own exit
# code for it is 1, which a caller would read as "an answer differs".
trap {
    Write-Host "[SETUP] $($_.Exception.Message)"
    exit 2
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ask = Join-Path $PSScriptRoot 'ask-server.ps1'
# A limit no list reaches.
$uncut = 10000000

if ([bool]$Tool -eq [bool]$CallsFile) {
    Write-Host "[SETUP] Give exactly one of -Tool and -CallsFile."
    exit 2
}
if ($Tool -and (-not $Over -or -not $Assembly)) {
    Write-Host "[SETUP] -Tool needs -Over Types or -Over Methods, and -Assembly."
    exit 2
}
if ($CallsFile -and (-not $AllowRoot -or -not (Test-Path $CallsFile))) {
    Write-Host "[SETUP] -CallsFile needs an existing file and -AllowRoot."
    exit 2
}
if ($Step -lt 1 -or $Chunks -lt 1) {
    Write-Host "[SETUP] -Step and -Chunks start at 1."
    exit 2
}
if ($Arguments -is [string]) { $Arguments = $Arguments | ConvertFrom-Json -AsHashtable }
if ($null -eq $Arguments) { $Arguments = @{} }

if (-not (Test-Path $Old)) {
    Write-Host "[SETUP] ILens executable not found: $Old"
    exit 2
}
$Old = (Resolve-Path $Old).Path
if (-not $New) {
    Write-Host "Building Core (Release)..."
    dotnet build (Join-Path $repoRoot 'Core' 'Core.csproj') -c Release -v q | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[SETUP] Core build failed."
        exit 2
    }
    $found = Get-ChildItem (Join-Path $repoRoot 'Build' 'bin' 'Core' 'Release') -Recurse -Filter 'ILens.exe' -ErrorAction SilentlyContinue
    $New = $found | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $New -or -not (Test-Path $New)) {
    Write-Host "[SETUP] ILens executable not found: $New"
    exit 2
}
$New = (Resolve-Path $New).Path
$builds = [ordered]@{ old = $Old; new = $New }

$roots = @($AllowRoot | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })
$assemblies = @($Assembly | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })

# The answers in the output of ask-server.ps1 for a calls file, in the order of
# the calls: each follows a line that names its call and how long it took.
function Split-Answers([string[]]$lines) {
    $answers = [System.Collections.Generic.List[object]]::new()
    $current = $null
    foreach ($line in $lines) {
        if ($line -match '^===== (\S+) (\{.*\}) \((\d+) ms\)$') {
            $current = [pscustomobject]@{
                Call = "$($Matches[1]) $($Matches[2])"; Milliseconds = [int]$Matches[3]
                Lines = [System.Collections.Generic.List[string]]::new()
            }
            $answers.Add($current)
        }
        elseif ($null -ne $current) { $current.Lines.Add($line) }
    }
    return $answers
}

# Asks one build a list of calls in this process and returns the answers.
function Get-Answers([string]$exe, [string[]]$allowRoots, [object[]]$calls, [string]$name) {
    $file = Join-Path $OutputDirectory "$name.json"
    ConvertTo-Json @($calls) -Depth 20 | Set-Content $file -Encoding utf8
    $output = & $ask -ILensExe $exe -AllowRoot $allowRoots -CallsFile $file -TimeoutSeconds $TimeoutSeconds 6>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -eq 2) { throw "Asking $exe failed: $($output -join ' ')" }
    # An answer arrives as one text with its line breaks inside.
    return Split-Answers @(($output -join "`n") -split "`r?`n")
}

# The result lines of a list answer: everything under the header but the lines
# that close a list.
function Get-ListLines($answer) {
    @($answer.Lines | Select-Object -Skip 1 | Where-Object { $_ -and $_ -notlike 'Not shown:*' -and $_ -notlike '... (*' })
}

# Whether the tool declares a parameter of that name, read from the tool list of the new build.
function Test-ToolParameter([string]$tool, [string]$parameter, [string[]]$allowRoots) {
    $declared = & $ask -ILensExe $New -AllowRoot $allowRoots -ToolParameters 6>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -eq 2) { throw "Reading the tool list failed: $($declared -join ' ')" }
    $inTool = $false; $known = $false
    foreach ($line in $declared) {
        if ($line -notmatch '^\s') { $inTool = $line -eq $tool; $known = $known -or $inTool; continue }
        if ($inTool -and $line -match "^\s+$([regex]::Escape($parameter))\s") { return $true }
    }
    if (-not $known) { throw "The new build has no tool '$tool'." }
    return $false
}

# The calls for one assembly: the template once for each type, or for each method.
function Get-TemplateCalls([string]$assemblyPath, [string[]]$allowRoots, [string]$label, [bool]$addLimit) {
    $listed = Get-Answers $New $allowRoots @(@{ name = 'find_types'; arguments = @{
        assembly = $assemblyPath; accessibility = 'all'; limit = $uncut } }) "$label-types"
    # A function hands back an array of one element as the element itself, so without
    # @() a list of one name would be a string, and $all[$i] one of its characters.
    $all = @(Get-ListLines $listed[0])
    $header = if ($all.Count) { "^$($all.Count) types?:" } else { '^No types$' }
    if ($listed[0].Lines[0] -notmatch $header) {
        throw "The type list of $assemblyPath does not match its header: $($listed[0].Lines[0]) against $($all.Count) names."
    }
    $types = @(for ($i = 0; $i -lt $all.Count; $i += $Step) { $all[$i] })

    $targets = [System.Collections.Generic.List[hashtable]]::new()
    # The types whose methods the new build could not list, each with its answer. Their
    # methods are not asked about, which the run has to say.
    $notListed = [System.Collections.Generic.List[string]]::new()
    if ($Over -eq 'Types') {
        foreach ($type in $types) { $targets.Add(@{ type_name = $type }) }
    }
    # Without a type there is no method to list, and Get-Answers given no call writes
    # the list as [null], which ask-server.ps1 cannot read.
    elseif ($types.Count) {
        $listCalls = $types | ForEach-Object {
            @{ name = 'find_methods'; arguments = @{ assembly = $assemblyPath; type_name = $_; accessibility = 'all'; limit = $uncut } }
        }
        $index = 0
        foreach ($answer in Get-Answers $New $allowRoots $listCalls "$label-methods") {
            $type = $types[$index++]
            if ($answer.Lines.Count -eq 0 -or $answer.Lines[0] -like 'Error:*') {
                $notListed.Add("$type | $(if ($answer.Lines.Count) { $answer.Lines[0] })")
                continue
            }
            foreach ($line in Get-ListLines $answer) {
                # A line reads "[static ]Type.Method(parameters) → return".
                $reference = $line -replace '^static ', ''
                $arrow = $reference.LastIndexOf(' → ')
                if ($arrow -gt 0) { $reference = $reference.Substring(0, $arrow) }
                if (-not $reference.StartsWith("$type.", [System.StringComparison]::Ordinal)) { continue }
                $rest = $reference.Substring($type.Length + 1)
                $open = $rest.IndexOf('(')
                if ($open -lt 1 -or -not $rest.EndsWith(')')) { continue }
                $target = @{ type_name = $type; member_name = $rest.Substring(0, $open) }
                $parameters = $rest.Substring($open + 1, $rest.Length - $open - 2)
                if ($parameters) { $target.parameter_types = @($parameters) } else { $target.parameter_count = 0 }
                $targets.Add($target)
            }
        }
    }

    $calls = foreach ($target in $targets) {
        $callArguments = @{ assembly = $assemblyPath }
        foreach ($key in $Arguments.Keys) { $callArguments[$key] = $Arguments[$key] }
        foreach ($key in $target.Keys) { $callArguments[$key] = $target[$key] }
        if ($addLimit) { $callArguments.limit = $uncut }
        @{ name = $Tool; arguments = $callArguments }
    }
    return @{ Calls = @($calls); TypesListed = $all.Count; Of = "$($types.Count) of $($all.Count) types"; NotListed = $notListed }
}

# Asks both builds the calls, in chunks side by side, and compares the answers
# call by call. Returns the counts and adds each difference to the report.
function Compare-Calls([object[]]$calls, [string[]]$allowRoots, [string]$label, [System.Collections.Generic.List[string]]$report) {
    $size = [math]::Ceiling($calls.Count / $Chunks)
    $jobs = @()
    $chunkSizes = @{}
    for ($i = 0; $i * $size -lt $calls.Count; $i++) {
        $part = @($calls | Select-Object -Skip ($i * $size) -First $size)
        $chunkSizes[$i] = $part.Count
        $callsPath = Join-Path $OutputDirectory "$label-calls-$i.json"
        ConvertTo-Json @($part) -Depth 20 | Set-Content $callsPath -Encoding utf8
        foreach ($tag in $builds.Keys) {
            $answersPath = Join-Path $OutputDirectory "$label-$tag-$i.txt"
            # A call may name its assembly by a path relative to the current directory.
            $jobs += Start-Job -WorkingDirectory $PWD.ProviderPath -ArgumentList $ask, $builds[$tag], $allowRoots, $callsPath, $TimeoutSeconds, $answersPath -ScriptBlock {
                param($ask, $exe, $allowRoots, $callsPath, $timeout, $answersPath)
                & $ask -ILensExe $exe -AllowRoot $allowRoots -CallsFile $callsPath -TimeoutSeconds $timeout *> $answersPath
                $LASTEXITCODE
            }
        }
    }
    $exitCodes = @($jobs | Wait-Job | Receive-Job)
    $jobs | Remove-Job
    if ($exitCodes -contains 2) { throw "A server failed while asking about $label; see the answer files in $OutputDirectory." }

    $counts = @{ Asked = $calls.Count; Alike = 0; ErrorsOnBoth = 0; Differ = 0; Missing = 0; OldMs = 0; NewMs = 0 }
    foreach ($i in $chunkSizes.Keys | Sort-Object) {
        $old = Split-Answers ([System.IO.File]::ReadAllLines((Join-Path $OutputDirectory "$label-old-$i.txt")))
        $new = Split-Answers ([System.IO.File]::ReadAllLines((Join-Path $OutputDirectory "$label-new-$i.txt")))
        for ($j = 0; $j -lt $chunkSizes[$i]; $j++) {
            if ($j -ge $old.Count -or $j -ge $new.Count -or $old[$j].Call -cne $new[$j].Call) { $counts.Missing++; continue }
            $x = $old[$j].Lines; $y = $new[$j].Lines
            $counts.OldMs += $old[$j].Milliseconds; $counts.NewMs += $new[$j].Milliseconds
            if (($x -join "`n") -ceq ($y -join "`n")) {
                $counts.Alike++
                if ($x.Count -gt 0 -and $x[0] -like 'Error:*') { $counts.ErrorsOnBoth++ }
                continue
            }
            $counts.Differ++
            $inOld = [System.Collections.Generic.HashSet[string]]::new([string[]]$x, [System.StringComparer]::Ordinal)
            $inNew = [System.Collections.Generic.HashSet[string]]::new([string[]]$y, [System.StringComparer]::Ordinal)
            $report.Add("DIFFERS: $($old[$j].Call)")
            $report.Add("    old: $(if ($x.Count) { $x[0] })")
            $report.Add("    new: $(if ($y.Count) { $y[0] })")
            foreach ($line in $y | Select-Object -Skip 1) { if (-not $inOld.Contains($line)) { $report.Add("    + $line") } }
            foreach ($line in $x | Select-Object -Skip 1) { if (-not $inNew.Contains($line)) { $report.Add("    - $line") } }
        }
    }
    return $counts
}

$addLimit = $Tool -and -not $Arguments.ContainsKey('limit') -and
    (Test-ToolParameter $Tool 'limit' @(@(Split-Path $assemblies[0]) + $roots | Select-Object -Unique))

# Made once the checks above have passed, so that a run they end leaves no folder.
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'Build' 'compare' (Get-Date -Format 'yyyyMMdd-HHmmss')
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$report = [System.Collections.Generic.List[string]]::new()
$report.Add("old: $Old")
$report.Add("new: $New")
$differ = 0; $missing = 0; $notListed = 0

if ($CallsFile) {
    $calls = @(Get-Content $CallsFile -Raw | ConvertFrom-Json -AsHashtable)
    $counts = Compare-Calls $calls $roots 'calls' $report
    "$CallsFile : $($counts.Asked) calls, $($counts.Alike) alike ($($counts.ErrorsOnBoth) of them the same error), " +
        "$($counts.Differ) differ, $($counts.Missing) missing. Time old $($counts.OldMs) ms, new $($counts.NewMs) ms."
    $differ += $counts.Differ; $missing += $counts.Missing
}
else {
    $number = 0
    foreach ($assemblyPath in $assemblies) {
        $allowRoots = @(@(Split-Path $assemblyPath) + $roots | Select-Object -Unique)
        $label = "{0}-{1}" -f (++$number), [System.IO.Path]::GetFileNameWithoutExtension($assemblyPath)
        $template = Get-TemplateCalls $assemblyPath $allowRoots $label $addLimit
        $report.Add("== $assemblyPath")
        foreach ($type in $template.NotListed) { $report.Add("NOT LISTED: $type") }
        $notListedPart = if ($template.NotListed.Count) { ", types whose methods were not listed: $($template.NotListed.Count)" }
        $notListed += $template.NotListed.Count
        # Nothing to ask about is a normal state, as for a facade that only forwards
        # types: the assembly is named and passed over without changing the exit code.
        if ($template.Calls.Count -eq 0) {
            $nothing = if ($template.TypesListed -eq 0) { 'no types listed' } else { "no methods listed over $($template.Of)" }
            $report.Add("NOTHING TO ASK: $nothing")
            "$([System.IO.Path]::GetFileName($assemblyPath)) : nothing asked, $nothing$notListedPart."
            continue
        }
        $counts = Compare-Calls $template.Calls $allowRoots $label $report
        "$([System.IO.Path]::GetFileName($assemblyPath)) : $($counts.Asked) calls over $($template.Of), $($counts.Alike) alike " +
            "($($counts.ErrorsOnBoth) of them the same error), $($counts.Differ) differ, $($counts.Missing) missing" +
            $notListedPart + ". Time old $($counts.OldMs) ms, new $($counts.NewMs) ms."
        $differ += $counts.Differ; $missing += $counts.Missing
    }
}

$reportPath = Join-Path $OutputDirectory 'report.txt'
$report | Set-Content $reportPath -Encoding utf8
"Report: $reportPath"

if ($missing -gt 0 -or $notListed -gt 0) {
    Write-Host "[FAIL] Answers missing: $missing. Types whose methods were not listed: $notListed. See the report and the answer files in $OutputDirectory."
    exit 2
}
if ($differ -gt 0) { exit 1 }
exit 0
