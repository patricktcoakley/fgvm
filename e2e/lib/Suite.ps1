<#
.SYNOPSIS
    Check whether the current verbosity level matches the given value.
.PARAMETER Verbosity
    The verbosity level to test (Quiet, Normal, or Detailed).
#>
function VerbosityIs {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Verbosity
    )

    $null -ne $script:Config -and $script:Config.Verbosity -eq $Verbosity
}

<#
.SYNOPSIS
    Print a summary line for a completed suite.
.PARAMETER Name
    Suite name.
.PARAMETER StartIndex
    Index into $script:Results where this suite's results begin.
.PARAMETER Duration
    Wall-clock time the suite took to run.
#>
function WriteSuiteSummary {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [int] $StartIndex,

        [Parameter(Mandatory = $true, Position = 2)]
        [timespan] $Duration
    )

    if (-not (VerbosityIs "Normal")) {
        return
    }

    $suiteResults = [System.Collections.Generic.List[object]]::new()
    for ($index = $StartIndex; $index -lt $script:Results.Count; $index++) {
        $result = $script:Results[$index]
        if ($result.Suite -eq $Name) {
            [void] $suiteResults.Add($result)
        }
    }

    if ($suiteResults.Count -eq 0) {
        Write-Information "suite - ${Name}: 0 tests ($([int] $Duration.TotalMilliseconds)ms)"
        return
    }

    $failedCount = 0
    foreach ($result in $suiteResults) {
        if (-not $result.Passed) {
            $failedCount++
        }
    }

    $passedCount = $suiteResults.Count - $failedCount
    if ($failedCount -eq 0) {
        Write-Information "suite - ${Name}: $passedCount passed ($([int] $Duration.TotalMilliseconds)ms)"
        return
    }

    Write-Information "suite - ${Name}: $passedCount/$($suiteResults.Count) passed ($([int] $Duration.TotalMilliseconds)ms)"
}

<#
.SYNOPSIS
    Extract the first user-script location from an error record's stack trace.
.DESCRIPTION
    Walks the script stack trace and returns the first frame outside the
    library directory. Falls back to InvocationInfo if no external frame is found.
.PARAMETER ErrorRecord
    The error record to analyse.
#>
function FormatStepFailureLocation {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [System.Management.Automation.ErrorRecord] $ErrorRecord
    )

    foreach ($frame in @([string] $ErrorRecord.ScriptStackTrace -split [System.Environment]::NewLine)) {
        if ($frame -notmatch '^\s*at .+, (?<path>.*): line (?<line>\d+)$') {
            continue
        }

        $scriptPath = $Matches["path"]
        if ([string]::IsNullOrWhiteSpace($scriptPath) -or $scriptPath -eq "<No file>") {
            continue
        }

        try {
            $fullPath = [System.IO.Path]::GetFullPath($scriptPath)
            $libraryRoot = [System.IO.Path]::GetFullPath($script:LibraryRoot)
            if ($fullPath.StartsWith($libraryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
        }
        catch {
            Write-Debug "Path resolution failed for '$scriptPath': $($_.Exception.Message)"
        }

        return "at ${scriptPath}:$($Matches["line"])"
    }

    $invocation = $ErrorRecord.InvocationInfo
    if ($null -ne $invocation -and -not [string]::IsNullOrWhiteSpace($invocation.ScriptName)) {
        return "at $($invocation.ScriptName):$($invocation.ScriptLineNumber)"
    }

    ""
}

<#
.SYNOPSIS
    Run a named step inside a test.
.DESCRIPTION
    Steps provide named substeps within a test. Failures include the step
    name and a source-location hint in the error message.
.PARAMETER Name
    Step name for display and error reporting.
.PARAMETER Body
    Script block containing the step logic.
#>
function Step {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    if (VerbosityIs "Detailed") {
        Write-Information "step - $Name"
    }

    try {
        & $Body
        $stopwatch.Stop()

        if (VerbosityIs "Detailed") {
            Write-Information "ok - step - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        }
    }
    catch {
        $stopwatch.Stop()

        if (VerbosityIs "Detailed") {
            Write-Information "not ok - step - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        }

        $location = FormatStepFailureLocation $_
        $message = "step: $Name"
        if (-not [string]::IsNullOrWhiteSpace($location)) {
            $message = "$message`n$location"
        }

        throw "$message`n$($_.Exception.Message)"
    }
}

<#
.SYNOPSIS
    Define a test suite with a name and body.
.DESCRIPTION
    Suites group related tests together for reporting. The body typically
    contains Test and Step calls. Timing and pass/fail summary is printed
    automatically.
.PARAMETER Name
    Suite name displayed in output.
.PARAMETER Body
    Script block containing Test definitions.
#>
function Suite {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    $previousSuite = $script:CurrentSuite
    $script:CurrentSuite = $Name
    $startIndex = $script:Results.Count
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    if (VerbosityIs "Detailed") {
        Write-Information "suite - $Name"
    }

    try {
        & $Body
    }
    finally {
        $stopwatch.Stop()
        WriteSuiteSummary $Name $startIndex $stopwatch.Elapsed
        $script:CurrentSuite = $previousSuite
    }
}

<#
.SYNOPSIS
    Define an individual test with automatic context setup and teardown.
.DESCRIPTION
    Creates an isolated E2EContext (with temp directories) before running the
    body, and tears it down afterwards. The $Context global variable is set
    so test scripts can access paths. Failures in the body or cleanup are
    recorded as test results.
.PARAMETER Name
    Test name displayed in output and used in result tracking.
.PARAMETER Body
    Script block containing test logic and assertions.
#>
function Test {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    if ($null -eq $script:Config) {
        throw "The e2e run has not been initialized."
    }

    $suite = if ([string]::IsNullOrWhiteSpace($script:CurrentSuite)) {
        "e2e"
    }
    else {
        $script:CurrentSuite
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $previousContext = $script:CurrentContext
    $context = NewContext $Name
    $script:CurrentContext = $context

    # Test scriptblocks are invoked from this module, so expose Context where the test file can resolve it.
    $hadGlobalContext = Test-Path -Path "Variable:global:Context"
    $previousGlobalContext = if ($hadGlobalContext) {
        Get-Variable -Name Context -Scope Global -ValueOnly
    }
    else {
        $null
    }

    Set-Variable -Name Context -Scope Global -Value $context

    $bodyError = $null
    $cleanupError = $null

    try {
        & $Body
    }
    catch {
        $bodyError = $_
    }

    try {
        RemoveContext $context

        if (Test-Path -LiteralPath $context.RootPath) {
            throw "Temporary test root was not removed: $($context.RootPath)"
        }
    }
    catch {
        $cleanupError = $_
    }
    finally {
        $script:CurrentContext = $previousContext
        if ($hadGlobalContext) {
            Set-Variable -Name Context -Scope Global -Value $previousGlobalContext
        }
        else {
            Remove-Variable -Name Context -Scope Global -ErrorAction SilentlyContinue
        }
    }

    $stopwatch.Stop()

    if ($null -eq $bodyError -and $null -eq $cleanupError) {
        AddResult $suite $Name $true $stopwatch.Elapsed
        if (VerbosityIs "Detailed") {
            Write-Information "ok - $suite - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        }
        return
    }

    $message = if ($null -ne $bodyError) {
        $bodyError.Exception.Message
    }
    else {
        $cleanupError.Exception.Message
    }

    AddResult $suite $Name $false $stopwatch.Elapsed $message
    Write-Information "not ok - $suite - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
    Write-Information $message
}
