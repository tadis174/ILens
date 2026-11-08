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
#   -Tool <name> [-Arguments <hashtable or JSON object>]
#           -Over Assemblies|Types|Methods|Accessors|Properties|Events|Fields
#                      The tool is asked once for each assembly or pair, once for
#                      each of its types, or once for each method, accessor,
#                      property, event or field of each type. The script adds
#                      assembly to the arguments given, or assembly_a and
#                      assembly_b for a pair, type_name for a type, and
#                      member_name for a member. A method is asked with its
#                      parameter types as find_methods prints them, or a parameter
#                      count of 0, and with its return type where an assembly
#                      lists another method of the type with that name and those
#                      parameters and the tool takes return_type. An accessor is
#                      asked by its IL name, get_ or set_ before a property's name
#                      and add_ or remove_ before an event's, after the
#                      interface's name for an explicit implementation, with its
#                      number of parameters, and with its parameter types where an
#                      assembly lists another accessor of the type with that name
#                      and number, as those of two indexers of one name can. A
#                      property, event or field is asked once by the name
#                      list_members writes, an indexer by its name, Item unless
#                      IndexerName gives another; where several indexers share
#                      it, the answer is an error that names their accessors. A
#                      run that would give the tool an argument it does not take,
#                      or not give it one it requires, stops before asking
#                      anything. Where the tool takes a limit and none is given,
#                      the script sets one that cuts nothing, since a cut list
#                      hides the differences below the cut; compare gets none for
#                      a member, since it refuses one there. Types, methods and
#                      the other members are listed by the new build, through
#                      find_types, find_methods and list_members, with
#                      accessibility all and without what the compiler generated;
#                      a pair's are those either of its assemblies lists.
#                      find_methods lists no accessor, so -Over Methods asks none.
#                      Below -Over Assemblies, every assembly's types are listed
#                      first and then their methods or members, through the new
#                      build's servers. All calls of a run are asked through one
#                      set of servers for each build, as a calls file is, so that
#                      a run over a corpus starts its servers once. A call's
#                      arguments are written in the order of their names, so that
#                      two runs write each call alike.
#   -CallsFile <path>  A JSON array of calls, [{ "name": ..., "arguments": {...} }, ...],
#                      asked of both builds as it is, except that a call to a tool
#                      that takes a limit and gives none gets one that cuts
#                      nothing, as with -Tool. Needs -AllowRoot.
#   -Recheck <dir>     The output directory of an earlier run, checked again
#                      against -Expect from the calls and answers it holds, for a
#                      rule found wrong after the run. It builds nothing and asks
#                      neither build, takes no option but -Expect, writes one line
#                      for all calls and its report to recheck-report.txt there.
#                      An assembly or type the run could not list, which its
#                      report names, ends the re-check with exit 2, as the run.
#
# Options:
#   -Expect <path>     The differences expected, written before the run: a
#                      PowerShell script that returns one hashtable for each class
#                      of difference, as "Rules" below says. Each answer that
#                      differs goes into the first class whose rule it fits.
#   -Old <path>        The executable of the build to compare against. Needed by
#                      -Tool and -CallsFile.
#   -New <path>        The executable of the build to check. Without it the script
#                      builds Core (Release) and takes that build output.
#   -Assembly <path>[,<path>...]
#                      The assemblies to ask about, with -Tool. Each one's
#                      directory is an allow-root unless an -AllowRoot holds it.
#   -Pairs <path>      The pairs of assemblies to ask about, with -Tool compare in
#                      place of -Assembly: a text file with one pair a line, the
#                      path of A, a tab and the path of B. Each one's directory is
#                      an allow-root unless an -AllowRoot holds it.
#   -AllowRoot <dir>[,<dir>...]
#                      Further directories the servers may read assemblies from.
#                      A server looks for an assembly's references in its own
#                      directory and then at the top of each allow-root, so where
#                      the assemblies lie in directories no -AllowRoot holds, one
#                      assembly's reference can resolve from another's directory.
#                      An -AllowRoot that holds them all, such as a corpus's
#                      folder, gives each the references a server for it alone
#                      finds, and keeps the servers' command line below the
#                      32,767 characters Windows allows, which some 300
#                      directories pass; a run whose command line would pass it
#                      stops before asking anything. A directory a link or
#                      junction below an -AllowRoot leads to stays an allow-root.
#   -Step <n>          With -Over at any level but Assemblies, every nth type only.
#   -Chunks <n>        The servers side by side for each build, and for the
#                      listing. Default 2. A server asks its calls one assembly or
#                      pair after another. One with many calls is shared out over
#                      all servers, so that its calls, when they are slow, do not
#                      hold up one server; any other goes whole to the server with
#                      the fewest calls.
#   -OutputDirectory <dir>
#                      Where the calls, the answers and the report go. Default: a
#                      new folder under Build/compare.
#   -TimeoutSeconds <n>
#                      Gives up when one answer takes longer. Default 900.
#
# Rules: the script given as -Expect returns its classes in the order they are
# tried, each a hashtable with Name and one of two forms of rule.
#   Name               What the summary lines and the report call the class.
#   Tool               Only calls of this tool.
#   Arguments          Only calls whose arguments hold these values, as
#                      @{ kind = 'used_by' }, each value written as the call's
#                      line writes it, a list as a list.
#   Calls              The calls the class predicts, each by the values of some
#                      of its arguments, as Arguments gives them. Each must differ
#                      and fit a class that predicts it; it fits no other class,
#                      and the class fits no other call.
#   Count              The number of answers the class predicts.
#   The first form says what may change, by .NET regular expressions matched
#   case-sensitively anywhere in a line; a change it does not allow keeps an
#   answer out of the class.
#   Old, New           The first line of the old or of the new answer. Without
#                      either, the first lines must be alike.
#   Added, Removed     Every line the report gives as added, or as removed.
#                      Without it, there may be none.
#   Reordered          $true: the lines both answers share may stand in another
#                      order.
#   The second form is a script block in their place:
#   Test               Gets one object with Call, the call's name and arguments,
#                      Old and New, each answer's lines, and Added and Removed,
#                      and returns $true or $false. It runs in a module of its
#                      own after the rules file has returned, so it sees a
#                      variable the file sets only through GetNewClosure(), and
#                      none of this script's functions.
#   A rule the run cannot apply as written, such as an unknown key, a pattern
#   that does not compile, a class that allows no change or a prediction that
#   names no call of the run, ends the run with exit 2 before the comparison's
#   calls are asked.
#
# Output: for each assembly or pair a line, or one for all calls with -Over
# Assemblies and with -CallsFile, with the calls asked, the answers that are
# alike, how many of those are the same error on both builds, the answers that
# differ and the call time summed for each build. The sums of servers side by side
# drift from run to run, so they do not measure what a change costs; that takes runs
# of each build alone, one after another and alternating, which this script does not
# make. An assembly or pair with nothing to ask
# about, one that lists no type or none of whose types lists a member of the kind
# asked, gets a line saying so in place of the counts, and one in the report; it
# does not change the exit code. One with an assembly whose types the new build
# cannot list, such as a file without metadata, gets a line saying so and the
# answer in the report, and the run asks the others and ends with exit 2. The lines
# come once every call is answered. The report, report.txt in the output directory,
# names the builds, and -Expect's file with the time it was last written where one
# is given, and holds each differing answer, in the order of the calls of each assembly or pair
# or of the calls file: the call, the first line of both answers, the lines
# below it that one build gave more often than the other, each as often as it is
# over, and a line "~ shared lines in another order" where the lines below it that
# both answers hold equally often stand in another order.
#
# With -Expect, each line gives how many of the answers that differ fit no class,
# and the lines after them give each class's answers, its Count where it has one,
# and its call time, then the answers in no class, the predicted calls that
# answered alike, and whether the run went as expected. In the report an answer
# that fits a class is headed "EXPECTED <class>:" in place of "DIFFERS:", one in no
# class that a class predicted has a line "predicted:" naming the class, and a
# predicted call that answered alike has an entry "ALIKE:".
#
# Example:
#   Tests/compare-builds.ps1 -Old C:\keep\before\ILens.exe `
#       -Assembly Build/bin/Tests/Debug/net10.0/CallShapes.dll `
#       -Tool analyze -Arguments @{ kind = 'used_by' } -Over Types
#   Tests/compare-builds.ps1 -Old C:\keep\before\ILens.exe -Pairs pairs.txt `
#       -Tool compare -Over Methods
#
# Exit codes: 0 = no answer differs, 1 = at least one answer differs, 2 = anything
# else: usage, build, a server that failed, an assembly whose types or a type whose
# methods or members could not be listed or read, or an answer that is missing.
# With -Expect, 0 = every answer that differs fits a class and every prediction
# and Count holds, 1 = not.
#
# Output cut short, by Select-Object -First or by Bash's head after pwsh, changes
# only what is shown: every assembly or call is still compared, the report
# written, and the exit code is that of the whole run.

[CmdletBinding()]
param(
    [string]$Old,
    [string]$New,
    [string[]]$Assembly,
    [string]$Pairs,
    [string[]]$AllowRoot,
    [string]$Tool,
    $Arguments,
    [ValidateSet('Assemblies', 'Types', 'Methods', 'Accessors', 'Properties', 'Events', 'Fields')][string]$Over,
    [string]$CallsFile,
    [string]$Recheck,
    [string]$Expect,
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
# Output cut short by the caller, as by Select-Object -First, stops the script at
# its next write with a PipelineStoppedException, which no catch block sees. A
# trap's continue goes on after the statement of the trap's own block that holds
# the write: here the top-level statement, which a write outside the loop over
# the assemblies ends, after what it reports is counted; the loop has a trap of
# its own, so that every assembly or pair is still reported and counted. A write
# therefore stands in a block with such a trap or last in a block inside it.
# Ctrl+C raises the same exception but sets $PSCmdlet.Stopping, which both traps
# pass on, so it ends the script, which runs only its finally blocks then. break on
# a cut, a trap for that type alone with an empty body, or a trap in a function that
# writes the line would end the calling script without a word.
trap {
    if ($_.Exception -is [System.Management.Automation.PipelineStoppedException] -and -not $PSCmdlet.Stopping) { continue }
    Write-Host "[SETUP] $($_.Exception.Message)"
    exit 2
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ask = Join-Path $PSScriptRoot 'ask-server.ps1'
# A limit no list reaches.
$uncut = 10000000
# For each level of -Over below the type: what it lists for a type, as the run's
# messages name it, and where list_members lists it, the kinds of member asked for.
$levels = @{
    Methods    = @{ Listed = 'methods' }
    Accessors  = @{ Listed = 'properties or events'; Kinds = @('property', 'event') }
    Properties = @{ Listed = 'properties'; Kinds = @('property') }
    Events     = @{ Listed = 'events'; Kinds = @('event') }
    Fields     = @{ Listed = 'fields'; Kinds = @('field') }
}

if (@($Tool, $CallsFile, $Recheck | Where-Object { $_ }).Count -ne 1) {
    Write-Host "[SETUP] Give exactly one of -Tool, -CallsFile and -Recheck."
    exit 2
}
# A re-check reads what a run wrote, so an option of a run beside it would let it
# read as asking what it does not.
if ($Recheck) {
    $options = @($PSBoundParameters.Keys | Where-Object { $_ -notin 'Recheck', 'Expect' -and $_ -notin [System.Management.Automation.PSCmdlet]::CommonParameters })
    if ($options.Count -or -not $Expect) {
        Write-Host "[SETUP] -Recheck takes -Expect and no other option."
        exit 2
    }
    if (-not (Test-Path $Recheck -PathType Container)) {
        Write-Host "[SETUP] Run directory not found: $Recheck"
        exit 2
    }
}
elseif (-not $Old) {
    Write-Host "[SETUP] -Tool and -CallsFile need -Old."
    exit 2
}
if ($Tool -and (-not $Over -or [bool]$Assembly -eq [bool]$Pairs)) {
    Write-Host "[SETUP] -Tool needs -Over and one of -Assembly and -Pairs."
    exit 2
}
if ($Tool -and $Pairs -and -not (Test-Path $Pairs -PathType Leaf)) {
    Write-Host "[SETUP] Pairs file not found: $Pairs"
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
# An option a mode passes over would let the run read as asking what it does not.
if (($CallsFile -or $Over -eq 'Assemblies') -and $Step -ne 1) {
    Write-Host "[SETUP] -Step applies to -Over at any level but Assemblies."
    exit 2
}
if ($CallsFile -and ($Assembly -or $Pairs)) {
    Write-Host "[SETUP] -Assembly and -Pairs go with -Tool; a calls file names its assemblies itself."
    exit 2
}

# The keys a class of -Expect may give, and those of its first form of rule.
$ruleKeys = 'Name', 'Tool', 'Arguments', 'Calls', 'Count', 'Old', 'New', 'Added', 'Removed', 'Reordered', 'Test'
$patternKeys = 'Old', 'New', 'Added', 'Removed'

# The classes the rules file returns, in its order, each with its patterns compiled. A
# rule the run cannot apply as written stops the run before it asks anything, since a
# class that took nothing or everything would decide the verdict unseen.
function Read-Rules([string]$path) {
    # The rules file runs in a module of its own, so that its Tests, which run later, see
    # none of the variables of the functions that call them.
    $items = @(& (New-Module -ScriptBlock { }) { param($file) & $file } $path)
    $names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    for ($i = 0; $i -lt $items.Count; $i++) {
        $item = $items[$i]
        if ($item -isnot [System.Collections.IDictionary]) { throw "Item $($i + 1) of what it returns is not a hashtable: $item" }
        $name = [string]$item['Name']
        $label = if ($name) { "The class '$name'" } else { "Class $($i + 1)" }
        $of = if ($name) { "of the class '$name'" } else { "of class $($i + 1)" }
        foreach ($key in $item.Keys) {
            if ($key -notin $ruleKeys) { throw "$label gives '$key', which is none of $($ruleKeys -join ', ')." }
        }
        if (-not $name) { throw "$label has no Name." }
        if (-not $names.Add($name)) { throw "$label is not the only class of that name." }
        $class = [pscustomobject]@{
            Name = $name; Tool = $null; Arguments = $null; Calls = $null; Count = $null
            Old = $null; New = $null; Added = $null; Removed = $null; Reordered = $false; Test = $null
            Answers = 0; OldMs = 0; NewMs = 0
        }
        if ($item.Contains('Tool')) {
            if ($item['Tool'] -isnot [string] -or -not $item['Tool']) { throw "The Tool $of is not the name of a tool." }
            $class.Tool = $item['Tool']
        }
        if ($item.Contains('Arguments')) {
            if ($item['Arguments'] -isnot [System.Collections.IDictionary] -or $item['Arguments'].Count -eq 0) {
                throw "The Arguments $of are not a hashtable of argument values."
            }
            $class.Arguments = $item['Arguments']
        }
        if ($item.Contains('Calls')) {
            $predictions = @($item['Calls'])
            if ($predictions.Count -eq 0 -or @($predictions | Where-Object { $_ -isnot [System.Collections.IDictionary] -or $_.Count -eq 0 }).Count) {
                throw "The Calls $of are not a list of hashtables of argument values."
            }
            $class.Calls = $predictions
        }
        if ($item.Contains('Count')) {
            if (-not ($item['Count'] -is [int] -or $item['Count'] -is [long]) -or $item['Count'] -lt 0) {
                throw "The Count $of is not a number of answers."
            }
            $class.Count = $item['Count']
        }
        $form = @($patternKeys | Where-Object { $item.Contains($_) })
        if ($item.Contains('Reordered')) {
            if ($item['Reordered'] -isnot [bool]) { throw "The Reordered $of is not `$true or `$false." }
            $class.Reordered = $item['Reordered']
        }
        if ($item.Contains('Test')) {
            $beside = @($form) + @('Reordered' | Where-Object { $item.Contains($_) })
            if ($beside.Count) { throw "$label gives Test beside $($beside -join ', '); a class gives one form of rule." }
            if ($item['Test'] -isnot [scriptblock]) { throw "The Test $of is not a script block." }
            $class.Test = $item['Test']
        }
        elseif (-not $form.Count -and -not $class.Reordered) {
            throw "$label allows no change: it gives none of $($patternKeys -join ', '), Reordered and Test."
        }
        foreach ($key in $form) {
            if ($item[$key] -isnot [string]) { throw "The $key $of is not a pattern." }
            try { $class.$key = [regex]::new($item[$key]) }
            catch { throw "The $key $of is not a regular expression: $(($_.Exception.InnerException ?? $_.Exception).Message)" }
        }
        $class
    }
}

$classes = $null
if ($Expect) {
    if (-not (Test-Path $Expect -PathType Leaf)) {
        Write-Host "[SETUP] Rules file not found: $Expect"
        exit 2
    }
    $Expect = (Resolve-Path $Expect).Path
    $expectWritten = (Get-Item $Expect).LastWriteTime
    try { $classes = @(Read-Rules $Expect) }
    catch {
        Write-Host "[SETUP] The rules in $Expect cannot be applied: $($_.Exception.Message)"
        exit 2
    }
}

if ($Arguments -is [string]) { $Arguments = $Arguments | ConvertFrom-Json -AsHashtable }
if ($null -eq $Arguments) { $Arguments = @{} }

# A re-check asks neither build, so it needs neither and builds nothing.
if (-not $Recheck) {
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
}

$roots = @($AllowRoot | Where-Object { $_ } | ForEach-Object { (Resolve-Path $_).Path })

# What the calls of -Tool ask about, each with the arguments that name it: an
# assembly, or a pair of assemblies, which compare takes as assembly_a and
# assembly_b.
$subjects = @(
    if ($Tool -and $Pairs) {
        $lineNumber = 0
        foreach ($line in Get-Content $Pairs) {
            $lineNumber++
            if (-not $line.Trim()) { continue }
            $fields = $line -split "`t"
            if ($fields.Count -ne 2) { throw "Line $lineNumber of $Pairs is not two paths separated by a tab: $line" }
            $a, $b = $fields | ForEach-Object { (Resolve-Path $_).Path }
            [pscustomobject]@{ Paths = @($a, $b); Arguments = [ordered]@{ assembly_a = $a; assembly_b = $b } }
        }
    }
    elseif ($Tool) {
        foreach ($path in $Assembly | Where-Object { $_ }) {
            $full = (Resolve-Path $path).Path
            [pscustomobject]@{ Paths = @($full); Arguments = @{ assembly = $full } }
        }
    }
)
if ($Tool -and $subjects.Count -eq 0) {
    Write-Host "[SETUP] $(if ($Pairs) { "The pairs file names no pair: $Pairs" } else { '-Assembly names no assembly.' })"
    exit 2
}

# The allow-roots for calls about these assemblies: the directory of each that lies
# under no -AllowRoot, and -AllowRoot. A server looks for an assembly's references in
# its own directory first, so one under an -AllowRoot needs no allow-root of its own,
# unless a link or junction below the -AllowRoot leads to it: the server checks the
# path an assembly's links resolve to, which then lies outside the -AllowRoot.
function Get-AllowRoots([string[]]$paths) {
    $separators = [char[]]'\/'
    $holders = @($roots | ForEach-Object { $_.TrimEnd($separators) })
    $links = @{}
    $outside = @($paths | ForEach-Object { Split-Path $_ } | Select-Object -Unique | Where-Object {
            $directory = $_.TrimEnd($separators)
            $holder = @($holders | Where-Object {
                    $directory -eq $_ -or $directory.StartsWith($_ + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
                })[0]
            if (-not $holder) { return $true }
            for ($part = $directory; $part.Length -gt $holder.Length; $part = Split-Path $part) {
                if (-not $links.ContainsKey($part)) {
                    $links[$part] = [bool]((Get-Item -LiteralPath $part -Force).Attributes -band [System.IO.FileAttributes]::ReparsePoint)
                }
                if ($links[$part]) { return $true }
            }
            $false
        })
    return @($outside + $roots | Select-Object -Unique)
}

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

# The result lines of a list answer: everything under the header but the lines
# that close a list.
function Get-ListLines($answer) {
    @($answer.Lines | Select-Object -Skip 1 | Where-Object { $_ -and $_ -notlike 'Not shown:*' -and $_ -notlike '... (*' })
}

# The parameters of each tool, read once from the tool list of the new build: a line
# that names a tool, then a line for each of its parameters, which ends with
# "required" where the tool requires it. $toolRequired holds those of each tool.
$toolParameters = $null
$toolRequired = $null
function Get-ToolParameters([string[]]$allowRoots) {
    if ($null -eq $script:toolParameters) {
        $declared = & $ask -ILensExe $New -AllowRoot $allowRoots -ToolParameters 6>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -eq 2) { throw "Reading the tool list failed: $($declared -join ' ')" }
        $script:toolParameters = @{}
        $script:toolRequired = @{}
        $parameters = $null
        $required = $null
        foreach ($line in $declared) {
            if ($line -notmatch '^\s') {
                $parameters = [System.Collections.Generic.HashSet[string]]::new()
                $required = [System.Collections.Generic.List[string]]::new()
                $script:toolParameters[$line] = $parameters
                $script:toolRequired[$line] = $required
            }
            elseif ($null -ne $parameters -and $line -match '^\s+(\S+)') {
                [void]$parameters.Add($Matches[1])
                if ($line.EndsWith('  required')) { $required.Add($Matches[1]) }
            }
        }
    }
    return $script:toolParameters
}

# Whether the tool declares a parameter of that name.
function Test-ToolParameter([string]$tool, [string]$parameter, [string[]]$allowRoots) {
    $tools = Get-ToolParameters $allowRoots
    if (-not $tools.ContainsKey($tool)) { throw "The new build has no tool '$tool'." }
    return $tools[$tool].Contains($parameter)
}

# Whether a call of a tool that takes a limit is answered with a list, which a limit
# cuts. compare answers a method with a diff of its two bodies, which no limit cuts,
# and refuses a limit there.
function Test-ListAnswer([string]$tool, $callArguments) {
    return -not ($tool -eq 'compare' -and $callArguments['member_name'])
}

# One call of the tool: the arguments that name the assembly or pair, those given,
# the target's, and with $addLimit a limit that cuts nothing where the answer is a
# list.
function New-Call($subject, [hashtable]$target, [bool]$addLimit) {
    $callArguments = @{}
    foreach ($part in $subject.Arguments, $Arguments, $target) {
        foreach ($key in $part.Keys) { $callArguments[$key] = $part[$key] }
    }
    if ($addLimit -and (Test-ListAnswer $Tool $callArguments)) { $callArguments.limit = $uncut }
    return ConvertTo-OrderedCall $Tool $callArguments
}

# A call with its arguments in the order of their names. A hashtable gives its keys in
# an order that changes from one process to the next, so that two runs would write one
# call in two ways.
function ConvertTo-OrderedCall([string]$name, $callArguments) {
    $names = [string[]]@($callArguments.Keys)
    [System.Array]::Sort($names, [System.StringComparer]::Ordinal)
    $sorted = [ordered]@{}
    foreach ($key in $names) { $sorted[$key] = $callArguments[$key] }
    return [ordered]@{ name = $name; arguments = $sorted }
}

# The places in a text where one of the characters stands outside angle, round and
# square brackets and outside a quoted literal; an opening bracket stands outside
# itself. A type such as Dictionary<string, int> holds a space and a comma, and so may
# a parameter's default, as "a, b" does. A literal begins a word, as after "= ", while
# an apostrophe within a name, which F# allows, belongs to the name.
function Get-OutsidePlaces([string]$text, [string]$characters) {
    $depth = 0
    $quote = ''
    for ($i = 0; $i -lt $text.Length; $i++) {
        $c = [string]$text[$i]
        if ($quote) {
            if ($c -eq '\') { $i++ } elseif ($c -eq $quote) { $quote = '' }
            continue
        }
        if ($depth -eq 0 -and $characters.Contains($c)) { $i }
        if (($c -eq '"' -or $c -eq "'") -and ($i -eq 0 -or ' ([<,'.Contains([string]$text[$i - 1]))) { $quote = $c }
        elseif ('<(['.Contains($c)) { $depth++ }
        elseif ('>)]'.Contains($c)) { $depth-- }
    }
}

# The parts of a text between the places where the separator stands outside brackets
# and quotes.
function Split-Outside([string]$text, [string]$separator) {
    $start = 0
    foreach ($place in Get-OutsidePlaces $text $separator) {
        $text.Substring($start, $place - $start)
        $start = $place + 1
    }
    $text.Substring($start)
}

# A member's line as list_members writes it, an obsolete one's after "[Obsolete] ",
# split into the member's name as the line writes it, the types of an indexer's
# parameters, the member's type, and the roles of a property's accessors, get, set or
# init; Roles is null for a line that ends in no accessors.
function Split-Declaration([string]$line) {
    $declaration = $line -replace '^\[Obsolete\] ', ''
    $roles = $null
    # A property's line ends with its accessors, each with its accessibility where that
    # differs from the property's, as in { get; private set; }; an accessibility ILSpy
    # reads as none is an empty word, as in {  get; }.
    if ($declaration -match '^(?<head>.+) \{ (?<accessors>(?:[a-z ]*(?:get|set|init); )*)\}$') {
        $declaration = $Matches.head
        $roles = @([regex]::Matches($Matches.accessors, '(get|set|init);') | ForEach-Object { $_.Groups[1].Value })
    }
    # A constant's ends with its value, which may hold any text a literal holds.
    elseif ($declaration.Contains(' = ')) {
        $declaration = $declaration.Substring(0, $declaration.IndexOf(' = ', [System.StringComparison]::Ordinal))
    }
    $words = @(Split-Outside $declaration ' ' | Where-Object { $_ })
    $name = $words[-1]
    $indexTypes = @()
    $open = @(Get-OutsidePlaces $name '[')
    if ($open.Count) {
        foreach ($parameter in Split-Outside $name.Substring($open[0] + 1, $name.Length - $open[0] - 2) ',') {
            # A parameter reads "[params ]Type name[ = default]", its type with the in
            # before it that a parameter passed as in takes, and its name empty where the
            # metadata gives none, so that the name is always the last part.
            $parts = @(Split-Outside $parameter.TrimStart() ' ')
            $end = [array]::IndexOf($parts, '=')
            if ($end -lt 0) { $end = $parts.Count }
            $first = if ($parts[0] -eq 'params') { 1 } else { 0 }
            $indexTypes += $parts[$first..($end - 2)] -join ' '
        }
        $name = $name.Substring(0, $open[0])
    }
    return [pscustomobject]@{ Name = $name; IndexTypes = $indexTypes; Type = $words[-2]; Roles = $roles }
}

# The methods of a find_methods answer, each with the target that asks about it, the
# signature that tells it from the type's other methods, its name and parameters, and
# as its detail its return type.
function Get-ListedMethods([string]$type, $answer) {
    foreach ($line in Get-ListLines $answer) {
        # A line reads "[static ]Type.Method(parameters) → return".
        $reference = $line -replace '^static ', ''
        $returns = $null
        $arrow = $reference.LastIndexOf(' → ')
        if ($arrow -gt 0) { $returns = $reference.Substring($arrow + 3); $reference = $reference.Substring(0, $arrow) }
        if (-not $reference.StartsWith("$type.", [System.StringComparison]::Ordinal)) { continue }
        $rest = $reference.Substring($type.Length + 1)
        $open = $rest.IndexOf('(')
        if ($open -lt 1 -or -not $rest.EndsWith(')')) { continue }
        $target = @{ type_name = $type; member_name = $rest.Substring(0, $open) }
        $parameters = $rest.Substring($open + 1, $rest.Length - $open - 2)
        if ($parameters) { $target.parameter_types = @($parameters) } else { $target.parameter_count = 0 }
        [pscustomobject]@{ Target = $target; Signature = $rest; Detail = $returns }
    }
}

# The members of a list_members answer, each with the target that asks about it and the
# signature that tells it from the type's other members. With -Over Accessors that is
# each accessor of each property and event, by its IL name and its number of
# parameters, with its parameter types as its detail, and a property line whose
# accessors this script cannot read goes to $unread; at the other levels each member
# by the name the line writes.
function Get-ListedMembers([string]$type, $answer, [System.Collections.Generic.List[string]]$unread) {
    $section = ''
    foreach ($line in $answer.Lines | Select-Object -Skip 1) {
        # A section opens with its count and kind, as "2 properties:", and indents its
        # members by two spaces.
        if ($line -match '^\d+ (\w+):$') { $section = $Matches[1]; continue }
        if (-not $line.StartsWith('  ')) { continue }
        $member = Split-Declaration $line.Substring(2)
        if ($Over -ne 'Accessors') {
            [pscustomobject]@{ Target = @{ type_name = $type; member_name = $member.Name }; Signature = $member.Name; Detail = $null }
            continue
        }
        # An accessor's IL name keeps an explicit implementation's interface before the
        # role and writes the member's own name without the @ C# sets before a keyword.
        $dot = $member.Name.LastIndexOf('.')
        $qualifier = $member.Name.Substring(0, $dot + 1)
        $own = $member.Name.Substring($dot + 1) -replace '^@', ''
        # An event's accessors take a handler of its type; a property's setter takes its
        # value after an indexer's parameters, an init accessor's as well.
        $accessors = [System.Collections.Generic.List[hashtable]]::new()
        if ($section -like 'event*') {
            $accessors.Add(@{ Role = 'add'; Types = @($member.Type) })
            $accessors.Add(@{ Role = 'remove'; Types = @($member.Type) })
        }
        elseif ($null -eq $member.Roles) {
            $unread.Add($line.Substring(2))
            continue
        }
        else {
            foreach ($role in $member.Roles) {
                if ($role -eq 'get') { $accessors.Add(@{ Role = 'get'; Types = @($member.IndexTypes) }) }
                else { $accessors.Add(@{ Role = 'set'; Types = @($member.IndexTypes) + $member.Type }) }
            }
        }
        foreach ($accessor in $accessors) {
            $il = $qualifier + $accessor.Role + '_' + $own
            $count = $accessor.Types.Count
            [pscustomobject]@{
                Target = @{ type_name = $type; member_name = $il; parameter_count = $count }
                Signature = "$il/$count"; Detail = $accessor.Types -join ', '
            }
        }
    }
}

# The calls for each assembly or pair: the template once for each type, or for each
# method or other member. A pair's types are those either of its assemblies lists, and
# a type's methods or members those either lists for it, each once. Every assembly's
# types are listed first and then every listed type's methods or members, each through
# the new build's servers side by side, so that a run over many assemblies starts
# servers once for each listing.
function Get-TemplateCalls([object[]]$subjects, [string[]]$allowRoots, [bool]$addLimit) {
    # Each assembly once, however many pairs name it.
    $assemblies = [System.Collections.Generic.List[string]]::new()
    $known = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($path in $subjects.Paths) { if ($known.Add($path)) { $assemblies.Add($path) } }
    $typeCalls = @(foreach ($path in $assemblies) { ConvertTo-OrderedCall 'find_types' @{ assembly = $path; accessibility = 'all'; limit = $uncut } })
    $answers = Get-Answers $typeCalls $allowRoots 'types'
    $typeLists = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::Ordinal)
    for ($a = 0; $a -lt $assemblies.Count; $a++) { $typeLists[$assemblies[$a]] = $answers[$a] }

    $templates = @(
        foreach ($subject in $subjects) {
            $paths = $subject.Paths
            # Each type with the assemblies that list it, by their place in the paths.
            $listedBy = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[int]]]::new([System.StringComparer]::Ordinal)
            # An assembly whose types the new build cannot list, such as a file without
            # metadata, with its answer. Its assembly or pair is not asked about, which the
            # run has to say.
            $unlisted = $null
            for ($k = 0; $k -lt $paths.Count; $k++) {
                $listed = $typeLists[$paths[$k]]
                if ($listed.Lines.Count -eq 0 -or $listed.Lines[0] -like 'Error:*') {
                    $unlisted = [pscustomobject]@{ Path = $paths[$k]; Answer = $(if ($listed.Lines.Count) { $listed.Lines[0] } else { 'no answer' }) }
                    $listedBy.Clear()
                    break
                }
                $names = @(Get-ListLines $listed)
                $header = if ($names.Count) { "^$($names.Count) types?:" } else { '^No types$' }
                if ($listed.Lines[0] -notmatch $header) {
                    throw "The type list of $($paths[$k]) does not match its header: $($listed.Lines[0]) against $($names.Count) names."
                }
                foreach ($name in $names) {
                    if (-not $listedBy.ContainsKey($name)) { $listedBy[$name] = [System.Collections.Generic.List[int]]::new() }
                    # A name an assembly lists twice is listed once, so that its methods are too.
                    if (-not $listedBy[$name].Contains($k)) { $listedBy[$name].Add($k) }
                }
            }
            # Sorted as find_types sorts, so that for one assembly -Step picks every nth type
            # of its list.
            $all = [string[]]@($listedBy.Keys)
            [System.Array]::Sort($all, [System.StringComparer]::Ordinal)
            [pscustomobject]@{
                Subject = $subject; ListedBy = $listedBy; Unlisted = $unlisted; All = $all
                Types = @(for ($i = 0; $i -lt $all.Count; $i += $Step) { $all[$i] })
                Targets = [System.Collections.Generic.List[hashtable]]::new()
                # The types whose methods or members the new build could not list, each with
                # its answer. Those are not asked about, which the run has to say.
                NotListed = [System.Collections.Generic.List[string]]::new()
            }
        }
    )

    if ($Over -eq 'Types') {
        foreach ($template in $templates) {
            foreach ($type in $template.Types) { $template.Targets.Add(@{ type_name = $type }) }
        }
    }
    else {
        # A type's methods come from find_methods, its other members from list_members,
        # each assembly's type once however many pairs name the assembly; the place of each
        # listing goes by the assembly's path and the type's name.
        $listCalls = [System.Collections.Generic.List[object]]::new()
        $listPlaces = [System.Collections.Generic.Dictionary[string, int]]::new([System.StringComparer]::Ordinal)
        foreach ($template in $templates) {
            foreach ($type in $template.Types) {
                foreach ($k in $template.ListedBy[$type]) {
                    $path = $template.Subject.Paths[$k]
                    if ($listPlaces.ContainsKey("$path|$type")) { continue }
                    $listPlaces["$path|$type"] = $listCalls.Count
                    if ($Over -eq 'Methods') {
                        $listCalls.Add((ConvertTo-OrderedCall 'find_methods' @{ assembly = $path; type_name = $type; accessibility = 'all'; limit = $uncut }))
                    }
                    else {
                        $listCalls.Add((ConvertTo-OrderedCall 'list_members' @{ assembly = $path; type_name = $type; kinds = $levels[$Over].Kinds; accessibility = 'all'; limit = $uncut }))
                    }
                }
            }
        }
        # What tells two methods or accessors of one signature apart: a method's return
        # type, where the tool takes one, and an accessor's parameter types.
        $hint = if ($Over -eq 'Accessors') { 'parameter_types' }
            elseif ($Over -eq 'Methods' -and (Test-ToolParameter $Tool 'return_type' $allowRoots)) { 'return_type' }
        $answers = Get-Answers $listCalls.ToArray() $allowRoots $(if ($Over -eq 'Methods') { 'methods' } else { 'members' })
        foreach ($template in $templates) {
            $paths = $template.Subject.Paths
            foreach ($type in $template.Types) {
                # The type's methods or members as each assembly that lists the type prints them.
                $listings = [System.Collections.Generic.List[object]]::new()
                foreach ($k in $template.ListedBy[$type]) {
                    $answer = $answers[$listPlaces["$($paths[$k])|$type"]]
                    $side = if ($paths.Count -gt 1) { ' in ' + ('A', 'B')[$k] }
                    if ($answer.Lines.Count -eq 0 -or $answer.Lines[0] -like 'Error:*') {
                        $template.NotListed.Add("$type$side | $(if ($answer.Lines.Count) { $answer.Lines[0] } else { 'no answer' })")
                        continue
                    }
                    $unread = [System.Collections.Generic.List[string]]::new()
                    $listings.Add(@(if ($Over -eq 'Methods') { Get-ListedMethods $type $answer } else { Get-ListedMembers $type $answer $unread }))
                    # A property whose accessors this script cannot read goes unasked, which the
                    # run says as it does for a type the new build could not list.
                    foreach ($line in $unread) { $template.NotListed.Add("$type$side | not read: $line") }
                }
                # Methods of one assembly that share their name and parameters and differ in
                # their return type alone, such as conversion operators, are asked by their
                # return type as well: asked by name and parameters, both builds answer with the
                # error that names them all. A method whose return type differs between the
                # assemblies of a pair is asked once without it, which compare answers with both
                # declarations. Accessors of one assembly that share their name and number of
                # parameters, those of indexers whose parameters differ in their types alone, are
                # asked by their parameter types as well, for the same reasons.
                $shared = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                foreach ($listing in $listings) {
                    $counts = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
                    foreach ($entry in $listing) { $counts[$entry.Signature] = 1 + $counts[$entry.Signature] }
                    foreach ($signature in $counts.Keys) { if ($counts[$signature] -gt 1) { [void]$shared.Add($signature) } }
                }
                # Each method or member once, by its signature, and by its detail where it is
                # asked by that.
                $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                foreach ($listing in $listings) {
                    foreach ($entry in $listing) {
                        $byDetail = $hint -and $entry.Detail -and $shared.Contains($entry.Signature)
                        if (-not $seen.Add($(if ($byDetail) { "$($entry.Signature) → $($entry.Detail)" } else { $entry.Signature }))) { continue }
                        if ($byDetail -and $hint -eq 'return_type') { $entry.Target.return_type = $entry.Detail }
                        elseif ($byDetail) { $entry.Target.parameter_types = @($entry.Detail) }
                        $template.Targets.Add($entry.Target)
                    }
                }
            }
        }
    }

    foreach ($template in $templates) {
        [pscustomobject]@{
            Subject = $template.Subject
            Calls = @(foreach ($target in $template.Targets) { New-Call $template.Subject $target $addLimit })
            TypesListed = $template.All.Count; Of = "$($template.Types.Count) of $($template.All.Count) types"
            NotListed = $template.NotListed; Unlisted = $template.Unlisted
        }
    }
}

# The calls of each server, by their places in the list. A server opens every
# assembly its calls name, so it asks them one assembly after another. An assembly
# with more calls than a server's share divided by the servers is cut into near-equal
# pieces, one for each server, since on one server its calls, when they are slow,
# keep that server busy long after the others; the first piece goes to the next
# server for each such assembly, so that the larger pieces of a cut that does not
# come out even fall on different servers. Every other assembly goes whole, the
# largest first, to the server with the fewest calls so far, so that a list of many
# small assemblies opens each in one server alone. An assembly is what a call's
# assembly, assembly_a and assembly_b name, each path in its full form, so that one
# assembly written two ways is one.
function Split-Calls([object[]]$calls) {
    $groups = [ordered]@{}
    for ($place = 0; $place -lt $calls.Count; $place++) {
        $arguments = $calls[$place]['arguments']
        $paths = if ($arguments) {
            foreach ($name in 'assembly', 'assembly_a', 'assembly_b') {
                if ($arguments[$name]) { [System.IO.Path]::GetFullPath([string]$arguments[$name], $PWD.ProviderPath) }
            }
        }
        $key = @($paths) -join '|'
        if (-not $groups.Contains($key)) { $groups[$key] = [System.Collections.Generic.List[int]]::new() }
        $groups[$key].Add($place)
    }
    # The comma keeps each list one element of the array, an empty one included.
    $shares = @(for ($i = 0; $i -lt $Chunks; $i++) { , [System.Collections.Generic.List[int]]::new() })
    $whole = [System.Collections.Generic.List[object]]::new()
    $next = 0
    foreach ($group in $groups.Values) {
        if ($group.Count * $Chunks * $Chunks -le $calls.Count) { $whole.Add($group); continue }
        for ($j = 0; $j -lt $group.Count; $j++) {
            $shares[($next + [math]::Floor($j * $Chunks / $group.Count)) % $Chunks].Add($group[$j])
        }
        $next = ($next + 1) % $Chunks
    }
    foreach ($group in @($whole | Sort-Object { $_.Count } -Descending -Stable)) {
        $fewest = $shares[0]
        foreach ($share in $shares) { if ($share.Count -lt $fewest.Count) { $fewest = $share } }
        $fewest.AddRange($group)
    }
    return , $shares
}

# The lines below the first of one answer that the other's lines below the first do
# not match, in the order of the first answer. A line is counted, not looked up, so
# that one lost from a pair of equal lines shows.
function Get-Surplus($lines, $others) {
    $left = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    foreach ($line in $others | Select-Object -Skip 1) { $left[$line] = 1 + $left[$line] }
    foreach ($line in $lines | Select-Object -Skip 1) {
        if ($left[$line] -gt 0) { $left[$line] = $left[$line] - 1 } else { $line }
    }
}

# Whether the lines below the first that both answers hold equally often stand in another
# order in one than in the other. A line whose count differs is left out: its added or
# removed copies show already, and which of its places changed is open.
function Test-Reordered($x, $y) {
    $inX = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    $inY = [System.Collections.Hashtable]::new([System.StringComparer]::Ordinal)
    foreach ($line in $x | Select-Object -Skip 1) { $inX[$line] = 1 + $inX[$line] }
    foreach ($line in $y | Select-Object -Skip 1) { $inY[$line] = 1 + $inY[$line] }
    $keptX = @($x | Select-Object -Skip 1 | Where-Object { $inX[$_] -eq $inY[$_] })
    $keptY = @($y | Select-Object -Skip 1 | Where-Object { $inX[$_] -eq $inY[$_] })
    return ($keptX -join "`n") -cne ($keptY -join "`n")
}

# A text two argument values share exactly when they are equal: a string or a number
# with its kind, a list as its items in order, a dictionary as its entries in the order
# of their names. Each part gives its length or its count first, so that keys written
# one after another cannot read alike for values that differ. A value of the rules and
# one of a call then match whether the call was made here, read from a calls file or
# from a run's files.
function Get-ValueKey($value) {
    if ($null -eq $value) { return 'n' }
    if ($value -is [string]) { return "s$($value.Length):$value" }
    if ($value -is [System.Collections.IDictionary]) {
        $names = [string[]]@($value.Keys)
        [System.Array]::Sort($names, [System.StringComparer]::OrdinalIgnoreCase)
        return "d$($names.Count):" + (@(foreach ($name in $names) { (Get-ValueKey $name) + (Get-ValueKey $value[$name]) }) -join '')
    }
    if ($value -is [System.Collections.IList]) { return "l$($value.Count):" + (@(foreach ($item in $value) { Get-ValueKey $item }) -join '') }
    if ($value -is [ValueType]) { $kind = 'v'; $text = [System.Convert]::ToString($value, [cultureinfo]::InvariantCulture) }
    else { $kind = 'j'; $text = $value | ConvertTo-Json -Compress -Depth 20 }
    return "$kind$($text.Length):$text"
}

# Whether the call's arguments hold every value the filter gives.
function Test-Arguments($callArguments, $filter) {
    if ($null -eq $callArguments) { return $false }
    foreach ($name in $filter.Keys) {
        if (-not $callArguments.Contains($name)) { return $false }
        if ((Get-ValueKey $callArguments[$name]) -cne (Get-ValueKey $filter[$name])) { return $false }
    }
    return $true
}

# For each call by its place, the names of the classes that predict it, or $null where
# none does. A prediction that names no call of the run, written wrong or for calls the
# run does not ask, would pass unseen, so the run stops on it before the comparison asks
# anything. The predictions are kept by the names of the arguments they give, so that a
# call is looked up once for each such set of names rather than against each prediction.
function Get-Predictions([object[]]$calls) {
    $predicted = [object[]]::new($calls.Count)
    $sets = [ordered]@{}
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($class in $classes) {
        foreach ($filter in @($class.Calls | Where-Object { $_ })) {
            $names = [string[]]@($filter.Keys)
            [System.Array]::Sort($names, [System.StringComparer]::Ordinal)
            $setKey = $names -join "`0"
            if (-not $sets.Contains($setKey)) {
                $sets[$setKey] = [pscustomobject]@{
                    Names = $names
                    Entries = [System.Collections.Generic.Dictionary[string, System.Collections.Generic.List[object]]]::new([System.StringComparer]::Ordinal)
                }
            }
            $key = @(foreach ($name in $names) { Get-ValueKey $filter[$name] }) -join ''
            if (-not $sets[$setKey].Entries.ContainsKey($key)) { $sets[$setKey].Entries[$key] = [System.Collections.Generic.List[object]]::new() }
            $entry = [pscustomobject]@{ Class = $class; Filter = $filter; Matched = $false }
            $sets[$setKey].Entries[$key].Add($entry)
            $entries.Add($entry)
        }
    }
    if ($entries.Count -eq 0) { return , $predicted }
    for ($place = 0; $place -lt $calls.Count; $place++) {
        $call = $calls[$place]
        $callArguments = $call['arguments']
        if ($null -eq $callArguments) { continue }
        foreach ($set in $sets.Values) {
            $parts = [System.Collections.Generic.List[string]]::new()
            foreach ($name in $set.Names) {
                if (-not $callArguments.Contains($name)) { break }
                $parts.Add((Get-ValueKey $callArguments[$name]))
            }
            $found = $null
            if ($parts.Count -lt $set.Names.Count -or -not $set.Entries.TryGetValue(($parts -join ''), [ref]$found)) { continue }
            foreach ($entry in $found) {
                $class = $entry.Class
                if ($class.Tool -and $class.Tool -cne $call['name']) { continue }
                if ($class.Arguments -and -not (Test-Arguments $callArguments $class.Arguments)) { continue }
                $entry.Matched = $true
                if ($null -eq $predicted[$place]) { $predicted[$place] = [System.Collections.Generic.List[string]]::new() }
                if (-not $predicted[$place].Contains($class.Name)) { $predicted[$place].Add($class.Name) }
            }
        }
    }
    $unmatched = @($entries | Where-Object { -not $_.Matched })
    if ($unmatched.Count) {
        $named = @($unmatched | Select-Object -First 5 | ForEach-Object { "$($_.Filter | ConvertTo-Json -Compress -Depth 20) of the class '$($_.Class.Name)'" })
        throw "$($unmatched.Count) predictions of the rules name no call of the run, such as $($named -join '; ')."
    }
    return , $predicted
}

# The class a differing answer fits, or $null: the first in the rules' order that may
# take the call and whose rule the difference fits. A call a class predicts may go only
# to the classes that predict it, and any other call only to the classes that predict
# none.
function Get-Class($call, [string[]]$x, [string[]]$y, [string[]]$added, [string[]]$removed, [bool]$reordered, $predictedBy, [string]$callLine) {
    $oldFirst = if ($x.Count) { $x[0] } else { '' }
    $newFirst = if ($y.Count) { $y[0] } else { '' }
    foreach ($class in $classes) {
        if ($predictedBy) { if (-not $predictedBy.Contains($class.Name)) { continue } }
        elseif ($class.Calls) { continue }
        if ($class.Tool -and $class.Tool -cne $call['name']) { continue }
        if ($class.Arguments -and -not (Test-Arguments $call['arguments'] $class.Arguments)) { continue }
        if ($class.Test) {
            $answer = [pscustomobject]@{ Call = $call; Old = $x; New = $y; Added = $added; Removed = $removed }
            try { $result = @(& $class.Test $answer) }
            catch { throw "The Test of the class '$($class.Name)' failed on $callLine; $($_.Exception.Message)" }
            if ($result.Count -ne 1 -or $result[0] -isnot [bool]) {
                $what = if ($result.Count -ne 1) { "$($result.Count) values" } elseif ($null -eq $result[0]) { '$null' } else { "a $($result[0].GetType().Name)" }
                throw "The Test of the class '$($class.Name)' gave $what on $callLine; it gives `$true or `$false."
            }
            if ($result[0]) { return $class }
            continue
        }
        # A change the class does not allow keeps the answer out of it.
        if (-not $class.Old -and -not $class.New -and $oldFirst -cne $newFirst) { continue }
        if ($class.Old -and -not $class.Old.IsMatch($oldFirst)) { continue }
        if ($class.New -and -not $class.New.IsMatch($newFirst)) { continue }
        if ($added.Count -and (-not $class.Added -or @($added | Where-Object { -not $class.Added.IsMatch($_) }).Count)) { continue }
        if ($removed.Count -and (-not $class.Removed -or @($removed | Where-Object { -not $class.Removed.IsMatch($_) }).Count)) { continue }
        if ($reordered -and -not $class.Reordered) { continue }
        return $class
    }
    return $null
}

# A call read from a calls file or a run's files in the form a call made here has: its
# arguments in a dictionary that keeps their order and, as a rule's own hashtable does,
# ignores the case of their names, and without the place a share's calls file gives it.
# A rule then matches a call, and a Test sees it, alike in a run and in its re-check.
function ConvertTo-Call($read) {
    $call = [ordered]@{}
    foreach ($key in $read.Keys) { if ($key -ne 'place') { $call[$key] = $read[$key] } }
    if ($call['arguments'] -is [System.Collections.IDictionary]) {
        $arguments = [ordered]@{}
        foreach ($name in $call['arguments'].Keys) { $arguments[$name] = $call['arguments'][$name] }
        $call['arguments'] = $arguments
    }
    return $call
}

# A call as a share's calls file holds it: its place in the run first, so that a
# re-check can give the answers in the run's order, then what the call gives.
# ask-server.ps1 reads only a call's name and arguments.
function Add-Place($call, [int]$place) {
    $placed = [ordered]@{ place = $place }
    foreach ($key in $call.Keys) { if ($key -ne 'place') { $placed[$key] = $call[$key] } }
    return $placed
}

# Asks the builds of the tags the calls, each through a server for each share side by
# side, and returns the shares. The calls and answers of each server stay in the output
# directory under the label.
function Invoke-Shares([object[]]$calls, [string[]]$allowRoots, [string]$label, [string[]]$tags) {
    $shares = Split-Calls $calls
    $jobs = @()
    # Ctrl+C stops this script but reaches neither the jobs nor their servers, each job
    # running in a console of its own. In a session that stays open after the stop, a job
    # left running asks its whole share and writes its answers into the run's folder; a
    # process that ends takes its jobs with it. So the finally block stops and removes the
    # jobs, without a condition, since $PSCmdlet.Stopping reads False inside it during the
    # stop; Stop-Job stops them one after another, and ask-server.ps1 kills its server
    # at once when its job is stopped while it awaits an answer. It finds the jobs by a
    # name of this call's own, so that it also ends one whose start the stop cut short
    # before $jobs held it, and no job it did not start.
    $name = "compare-builds-$label-$([guid]::NewGuid().ToString('N'))"
    try {
        for ($i = 0; $i -lt $shares.Count; $i++) {
            # A server with no calls is not started.
            if ($shares[$i].Count -eq 0) { continue }
            $callsPath = Join-Path $OutputDirectory "$label-calls-$i.json"
            ConvertTo-Json @(foreach ($place in $shares[$i]) { Add-Place $calls[$place] $place }) -Depth 20 | Set-Content $callsPath -Encoding utf8
            foreach ($tag in $tags) {
                $answersPath = Join-Path $OutputDirectory "$label-$tag-$i.txt"
                # A call may name its assembly by a path relative to the current directory.
                $jobs += Start-Job -Name $name -WorkingDirectory $PWD.ProviderPath -ArgumentList $ask, $builds[$tag], $allowRoots, $callsPath, $TimeoutSeconds, $answersPath -ScriptBlock {
                    param($ask, $exe, $allowRoots, $callsPath, $timeout, $answersPath)
                    & $ask -ILensExe $exe -AllowRoot $allowRoots -CallsFile $callsPath -TimeoutSeconds $timeout *> $answersPath
                    $LASTEXITCODE
                }
            }
        }
        $exitCodes = @($jobs | Wait-Job | Receive-Job)
    }
    finally {
        $own = @(Get-Job | Where-Object { $_.Name -eq $name })
        $own | Stop-Job
        $own | Remove-Job -Force
    }
    if ($exitCodes -contains 2) { throw "A server failed while asking about $label; see the answer files in $OutputDirectory." }
    return , $shares
}

# The answers of one build's server for a share, in the order of its calls.
function Read-Share([string]$label, [string]$tag, [int]$i) {
    return Split-Answers ([System.IO.File]::ReadAllLines((Join-Path $OutputDirectory "$label-$tag-$i.txt")))
}

# Asks the new build the calls and returns its answers in the order of the calls, with
# none where a server's answers run out.
function Get-Answers([object[]]$calls, [string[]]$allowRoots, [string]$label) {
    $shares = Invoke-Shares $calls $allowRoots $label 'new'
    $answers = [object[]]::new($calls.Count)
    for ($i = 0; $i -lt $shares.Count; $i++) {
        if ($shares[$i].Count -eq 0) { continue }
        $read = Read-Share $label 'new' $i
        for ($j = 0; $j -lt $shares[$i].Count -and $j -lt $read.Count; $j++) { $answers[$shares[$i][$j]] = $read[$j] }
    }
    return , $answers
}

# The calls of a run's output directory by their places, and the shares, each the
# places of its calls in the order its files hold them. A share's calls file gives each
# call's place; one written without places is read share by share, in the order of the
# numbers in the files' names.
function Read-Run {
    $pattern = '^calls-calls-(\d+)\.json$'
    $files = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object { $_.Name -match $pattern } |
            Sort-Object { [int]($_.Name -replace $pattern, '$1') })
    if ($files.Count -eq 0) { throw "$OutputDirectory holds no calls of a comparison, which a run writes as calls-calls-<n>.json." }
    $last = [int]($files[-1].Name -replace $pattern, '$1')
    # The comma keeps each list one element of the array, an empty one included.
    $shares = @(for ($i = 0; $i -le $last; $i++) { , [System.Collections.Generic.List[int]]::new() })
    $byPlace = [System.Collections.Generic.Dictionary[int, object]]::new()
    $placed = $null
    foreach ($file in $files) {
        $i = [int]($file.Name -replace $pattern, '$1')
        foreach ($call in @(Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable)) {
            if ($null -eq $placed) { $placed = $call.Contains('place') }
            if ($call.Contains('place') -ne $placed) { throw "The calls files in $OutputDirectory give some calls' places and not others'." }
            $place = if ($placed) { [int]$call['place'] } else { $byPlace.Count }
            if ($place -lt 0 -or $byPlace.ContainsKey($place)) { throw "$($file.Name) gives the place $place, which no call or another call holds." }
            $byPlace[$place] = ConvertTo-Call $call
            $shares[$i].Add($place)
        }
    }
    $calls = [object[]]::new($byPlace.Count)
    foreach ($place in $byPlace.Keys) {
        if ($place -ge $calls.Count) { throw "The calls files in $OutputDirectory leave out a place below $place." }
        $calls[$place] = $byPlace[$place]
    }
    return [pscustomobject]@{ Calls = $calls; Shares = $shares }
}

# Compares the answers both builds gave to the calls, share by share, as the files of
# the shares in the output directory hold them. Returns for each call, in their order,
# its outcome: Alike, ErrorOnBoth for answers alike that are an error, Differs with the
# class it fits where -Expect is given, or Missing; the time of each build's answer; the
# classes that predict the call; and the lines the report gives it, which a call that
# answers alike gets where a class predicts it. The answers are read one share at a time,
# both builds' together, so that a run holds those of one share of its calls rather than
# all of them.
function Compare-Calls([object[]]$calls, [object[]]$shares, [string]$label, [object[]]$predicted) {
    $outcomes = [object[]]::new($calls.Count)
    for ($i = 0; $i -lt $shares.Count; $i++) {
        if ($shares[$i].Count -eq 0) { continue }
        $old = Read-Share $label 'old' $i
        $new = Read-Share $label 'new' $i
        for ($j = 0; $j -lt $shares[$i].Count; $j++) {
            $place = $shares[$i][$j]
            if ($j -ge $old.Count -or $j -ge $new.Count -or $old[$j].Call -cne $new[$j].Call) {
                $outcomes[$place] = [pscustomobject]@{ Kind = 'Missing'; OldMs = 0; NewMs = 0; Class = $null; Predicted = $null; Report = $null }
                continue
            }
            $x = $old[$j].Lines; $y = $new[$j].Lines
            # Assigned rather than taken from an if's output, which would turn the list of a
            # single class into its name.
            $predictedBy = $null
            if ($predicted) { $predictedBy = $predicted[$place] }
            $lines = [System.Collections.Generic.List[string]]::new()
            if (($x -join "`n") -ceq ($y -join "`n")) {
                $kind = if ($x.Count -gt 0 -and $x[0] -like 'Error:*') { 'ErrorOnBoth' } else { 'Alike' }
                $entry = $null
                if ($predictedBy) {
                    $lines.Add("ALIKE: $($old[$j].Call)")
                    $lines.Add("    predicted: $($predictedBy -join ', ')")
                    $entry = $lines
                }
                $outcomes[$place] = [pscustomobject]@{ Kind = $kind; OldMs = $old[$j].Milliseconds; NewMs = $new[$j].Milliseconds; Class = $null; Predicted = $predictedBy; Report = $entry }
                continue
            }
            $added = [string[]]@(Get-Surplus $y $x)
            $removed = [string[]]@(Get-Surplus $x $y)
            $reordered = Test-Reordered $x $y
            $class = if ($Expect) { Get-Class $calls[$place] $x $y $added $removed $reordered $predictedBy $old[$j].Call }
            if ($class) { $lines.Add("EXPECTED $($class.Name): $($old[$j].Call)") }
            else {
                $lines.Add("DIFFERS: $($old[$j].Call)")
                if ($predictedBy) { $lines.Add("    predicted: $($predictedBy -join ', ')") }
            }
            $lines.Add("    old: $(if ($x.Count) { $x[0] })")
            $lines.Add("    new: $(if ($y.Count) { $y[0] })")
            foreach ($line in $added) { $lines.Add("    + $line") }
            foreach ($line in $removed) { $lines.Add("    - $line") }
            if ($reordered) { $lines.Add('    ~ shared lines in another order') }
            $outcomes[$place] = [pscustomobject]@{ Kind = 'Differs'; OldMs = $old[$j].Milliseconds; NewMs = $new[$j].Milliseconds; Class = $class; Predicted = $predictedBy; Report = $lines }
        }
    }
    return , $outcomes
}

# The counts of a summary line over the outcomes of $count calls from $first on. Adds
# the entries of the differing answers, and of the predicted calls that answered alike,
# to the report.
function Measure-Outcomes([object[]]$outcomes, [int]$first, [int]$count, [System.Collections.Generic.List[string]]$report) {
    $counts = @{ Asked = $count; Alike = 0; ErrorsOnBoth = 0; Differ = 0; Unexpected = 0; Missing = 0; OldMs = 0; NewMs = 0 }
    for ($j = $first; $j -lt $first + $count; $j++) {
        $outcome = $outcomes[$j]
        if ($outcome.Kind -eq 'Missing') { $counts.Missing++; continue }
        $counts.OldMs += $outcome.OldMs; $counts.NewMs += $outcome.NewMs
        if ($null -ne $outcome.Report) { $report.AddRange($outcome.Report) }
        if ($outcome.Kind -eq 'Differs') {
            $counts.Differ++
            if (-not $outcome.Class) { $counts.Unexpected++ }
            continue
        }
        $counts.Alike++
        if ($outcome.Kind -eq 'ErrorOnBoth') { $counts.ErrorsOnBoth++ }
    }
    return $counts
}

# The part of a summary line from the calls asked to the answers missing, $over saying
# what the calls were asked over. With -Expect it says how many differing answers fit no
# class.
function Format-Counts($counts, [string]$over) {
    $unexpected = if ($Expect) { " ($($counts.Unexpected) of them unexpected)" }
    return "$($counts.Asked) calls$over, $($counts.Alike) alike ($($counts.ErrorsOnBoth) of them the same error), " +
        "$($counts.Differ) differ$unexpected, $($counts.Missing) missing"
}

# The tallies of the lines after the summary lines under -Expect: each class's answers and
# time, which go into the class, and the answers in no class with their time and the
# predicted calls that answered alike, which it returns.
function Measure-Classes([object[]]$outcomes) {
    $tally = [pscustomobject]@{ Unexpected = 0; OldMs = 0; NewMs = 0; PredictedAlike = 0 }
    foreach ($outcome in $outcomes) {
        if ($null -eq $outcome -or $outcome.Kind -eq 'Missing') { continue }
        if ($outcome.Kind -ne 'Differs') {
            if ($outcome.Predicted) { $tally.PredictedAlike++ }
        }
        elseif ($outcome.Class) {
            $outcome.Class.Answers++
            $outcome.Class.OldMs += $outcome.OldMs; $outcome.Class.NewMs += $outcome.NewMs
        }
        else {
            $tally.Unexpected++
            $tally.OldMs += $outcome.OldMs; $tally.NewMs += $outcome.NewMs
        }
    }
    return $tally
}

if ($Tool) {
    $toolRoots = Get-AllowRoots $subjects[0].Paths
    # An argument the tool does not take makes every call the same error on both builds,
    # which the comparison counts as alike.
    $given = @($subjects[0].Arguments.Keys) + @($Arguments.Keys)
    if ($Over -ne 'Assemblies') { $given += 'type_name' }
    if ($Over -in 'Methods', 'Accessors') { $given += 'member_name', 'parameter_types', 'parameter_count' }
    elseif ($Over -in 'Properties', 'Events', 'Fields') { $given += 'member_name' }
    $refused = @($given | Where-Object { -not (Test-ToolParameter $Tool $_ $toolRoots) })
    if ($refused.Count) {
        $hint = if ($refused -contains 'assembly' -and (Test-ToolParameter $Tool 'assembly_a' $toolRoots)) { " $Tool takes two assemblies, which -Pairs gives." }
            elseif ($refused -contains 'assembly_a' -and (Test-ToolParameter $Tool 'assembly' $toolRoots)) { " $Tool takes one assembly, which -Assembly gives." }
        Write-Host "[SETUP] $Tool does not take $($refused -join ', '), which the calls of this run would give it.$hint"
        exit 2
    }
    # So does a parameter the tool requires and the run does not give.
    $lacking = @($script:toolRequired[$Tool] | Where-Object { $_ -notin $given })
    if ($lacking.Count) {
        Write-Host "[SETUP] $Tool requires $($lacking -join ', '), which the calls of this run would not give it."
        exit 2
    }
    # The allow-roots of every server of the run. Windows refuses a command line past
    # 32,767 characters, which some 300 directories pass, and a server that cannot start
    # would read as one that failed. A root goes on it as " --allow-root " and the path
    # in quotes.
    $runRoots = Get-AllowRoots @($subjects.Paths)
    $commandLine = [math]::Max($Old.Length, $New.Length)
    foreach ($root in $runRoots) { $commandLine += 16 + $root.Length }
    if ($commandLine -gt 32767) {
        Write-Host "[SETUP] The servers' $($runRoots.Count) allow-roots make a command line of about $commandLine characters, over the 32,767 Windows allows. Give an -AllowRoot that holds the assemblies' directories."
        exit 2
    }
}
$addLimit = $Tool -and -not $Arguments.ContainsKey('limit') -and (Test-ToolParameter $Tool 'limit' $toolRoots)

if ($CallsFile) {
    $calls = @(Get-Content $CallsFile -Raw | ConvertFrom-Json -AsHashtable | ForEach-Object { ConvertTo-Call $_ })
    # A call to a tool that takes a limit and gives none gets one that cuts nothing, as
    # with -Tool: cut at the tool's default, an answer would show the lines a change
    # pushes past the cut as removed. A call to a tool the new build lacks stays as it is.
    $tools = Get-ToolParameters $roots
    foreach ($call in $calls) {
        $name = $call['name']
        if (-not $name -or -not $tools.ContainsKey($name) -or -not $tools[$name].Contains('limit')) { continue }
        if ($null -eq $call['arguments']) { $call['arguments'] = [ordered]@{} }
        if (-not $call['arguments'].Contains('limit') -and (Test-ListAnswer $name $call['arguments'])) { $call['arguments']['limit'] = $uncut }
    }
}

# Where the calls are known before anything is listed, a prediction that names none of
# them ends the run before the output folder is made.
if ($Tool -and $Over -eq 'Assemblies') { $calls = @(foreach ($subject in $subjects) { New-Call $subject @{} $addLimit }) }
$predicted = $null
if ($Expect -and ($CallsFile -or $Over -eq 'Assemblies')) { $predicted = Get-Predictions $calls }

# Made once the checks above have passed, so that a run they end leaves no folder. A
# re-check reads and writes in the run's.
if ($Recheck) { $OutputDirectory = $Recheck }
elseif (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repoRoot 'Build' 'compare' (Get-Date -Format 'yyyyMMdd-HHmmss')
}
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path $OutputDirectory).Path

$report = [System.Collections.Generic.List[string]]::new()
if ($Recheck) { $report.Add("recheck: $OutputDirectory") }
else {
    $report.Add("old: $Old")
    $report.Add("new: $New")
}
if ($Expect) { $report.Add("expect: $Expect, written $($expectWritten.ToString('yyyy-MM-dd HH:mm:ss zzz'))") }
$differ = 0; $missing = 0; $notListed = 0; $unlistedSubjects = 0

if ($Recheck) {
    $run = Read-Run
    $predicted = Get-Predictions $run.Calls
    $outcomes = Compare-Calls $run.Calls $run.Shares 'calls' $predicted
    # An assembly or type the run could not list left no call, and the run's report names
    # it; the re-check gives those lines and ends with exit 2, as the run did.
    $runReport = Join-Path $OutputDirectory 'report.txt'
    $unlisted = @(if (Test-Path $runReport) { Get-Content $runReport | Where-Object { $_ -like 'NOT LISTED: *' } })
    $report.AddRange([string[]]$unlisted)
    $notListed += $unlisted.Count
    $counts = Measure-Outcomes $outcomes 0 $outcomes.Count $report
    $differ += $counts.Differ; $missing += $counts.Missing
    $unlistedPart = if ($unlisted.Count) { ", not listed in the run: $($unlisted.Count)" }
    "$OutputDirectory : $(Format-Counts $counts '')$unlistedPart. Time old $($counts.OldMs) ms, new $($counts.NewMs) ms."
}
elseif ($CallsFile -or $Over -eq 'Assemblies') {
    $callRoots = $roots
    $source = $CallsFile
    if ($Tool) {
        $callRoots = $runRoots
        $source = if ($Pairs) { $Pairs } else { 'Assemblies' }
    }
    $shares = Invoke-Shares $calls $callRoots 'calls' @($builds.Keys)
    $outcomes = Compare-Calls $calls $shares 'calls' $predicted
    $counts = Measure-Outcomes $outcomes 0 $outcomes.Count $report
    $differ += $counts.Differ; $missing += $counts.Missing
    "$source : $(Format-Counts $counts ''). Time old $($counts.OldMs) ms, new $($counts.NewMs) ms."
}
else {
    $templates = @(Get-TemplateCalls $subjects $runRoots $addLimit)
    # The calls of every assembly or pair, asked at once, each one's from its first place on.
    $calls = [System.Collections.Generic.List[object]]::new()
    foreach ($template in $templates) {
        $template | Add-Member -NotePropertyName First -NotePropertyValue $calls.Count
        $calls.AddRange($template.Calls)
    }
    if ($Expect) { $predicted = Get-Predictions $calls.ToArray() }
    $shares = Invoke-Shares $calls.ToArray() $runRoots 'calls' @($builds.Keys)
    $outcomes = Compare-Calls $calls.ToArray() $shares 'calls' $predicted
    foreach ($template in $templates) {
        # Goes on with this loop after a cut, as the comment on the trap at the top says.
        trap [System.Management.Automation.PipelineStoppedException] {
            if ($PSCmdlet.Stopping) { break }
            continue
        }
        $subject = $template.Subject
        $report.Add("== $($subject.Paths -join ' | ')")
        if ($template.Unlisted) { $report.Add("NOT LISTED: $($template.Unlisted.Path) | $($template.Unlisted.Answer)") }
        foreach ($type in $template.NotListed) { $report.Add("NOT LISTED: $type") }
        $notListedPart = if ($template.NotListed.Count) { ", types whose $($levels[$Over].Listed) were not listed: $($template.NotListed.Count)" }
        $notListed += $template.NotListed.Count
        $fileName = @($subject.Paths | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -join ' | '
        # An assembly whose types the new build could not list leaves its assembly or
        # pair unasked, which ends the run with exit 2.
        if ($template.Unlisted) {
            $unlistedSubjects++
            $whose = if ($subject.Paths.Count -gt 1) { " of $([System.IO.Path]::GetFileName($template.Unlisted.Path))" }
            $summary = "$fileName : nothing asked, types$whose not listed."
        }
        # Nothing to ask about is a normal state, as for a facade that only forwards
        # types: the assembly is named and passed over without changing the exit code.
        elseif ($template.Calls.Count -eq 0) {
            $nothing = if ($template.TypesListed -eq 0) { 'no types listed' } else { "no $($levels[$Over].Listed) listed over $($template.Of)" }
            $report.Add("NOTHING TO ASK: $nothing")
            $summary = "$fileName : nothing asked, $nothing$notListedPart."
        }
        else {
            $counts = Measure-Outcomes $outcomes $template.First $template.Calls.Count $report
            $differ += $counts.Differ; $missing += $counts.Missing
            $summary = "$fileName : $(Format-Counts $counts " over $($template.Of)")$notListedPart. " +
                "Time old $($counts.OldMs) ms, new $($counts.NewMs) ms."
        }
        $summary
    }
}

# Under -Expect, the classes and the verdict, all counted before the first line goes out.
if ($Expect) {
    $tally = Measure-Classes $outcomes
    $miscounted = @($classes | Where-Object { $null -ne $_.Count -and $_.Count -ne $_.Answers })
    $asExpected = $tally.Unexpected -eq 0 -and $tally.PredictedAlike -eq 0 -and $miscounted.Count -eq 0
    $verdict = @(
        foreach ($class in $classes) {
            $of = if ($null -ne $class.Count) { " of $($class.Count) predicted" }
            "Expected $($class.Name): $($class.Answers) answers$of. Time old $($class.OldMs) ms, new $($class.NewMs) ms."
        }
        "Unexpected: $($tally.Unexpected) answers. Time old $($tally.OldMs) ms, new $($tally.NewMs) ms."
        if (@($classes | Where-Object { $_.Calls }).Count) { "Predicted and alike: $($tally.PredictedAlike) calls." }
        if ($asExpected) { 'As expected.' }
        else {
            $parts = @(
                if ($tally.Unexpected) { "$($tally.Unexpected) answers in no class" }
                if ($tally.PredictedAlike) { "$($tally.PredictedAlike) predicted calls alike" }
                if ($miscounted.Count) { "another number of answers in $($miscounted.Name -join ', ')" }
            )
            "Not as expected: $($parts -join ', ')."
        }
    )
    foreach ($line in $verdict) {
        # Goes on with this loop after a cut, as the comment on the trap at the top says.
        trap [System.Management.Automation.PipelineStoppedException] {
            if ($PSCmdlet.Stopping) { break }
            continue
        }
        $line
    }
}

$reportPath = Join-Path $OutputDirectory $(if ($Recheck) { 'recheck-report.txt' } else { 'report.txt' })
$report | Set-Content $reportPath -Encoding utf8
"Report: $reportPath"

if ($missing -gt 0 -or $notListed -gt 0 -or $unlistedSubjects -gt 0) {
    $what = "Answers missing: $missing."
    if ($unlistedSubjects) { $what += " $(if ($Pairs) { 'Pairs with an assembly' } else { 'Assemblies' }) whose types were not listed: $unlistedSubjects." }
    if ($notListed) {
        $part = if ($Recheck) { " Assemblies or types the run could not list: $notListed." } else { " Types whose $($levels[$Over].Listed) were not listed: $notListed." }
        $what += $part
    }
    Write-Host "[FAIL] $what See the report and the answer files in $OutputDirectory."
    exit 2
}
if ($Expect) {
    if ($asExpected) { exit 0 }
    exit 1
}
if ($differ -gt 0) { exit 1 }
exit 0
