function VerbosityIs {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Verbosity
    )

    $null -ne $script:Config -and $script:Config.Verbosity -eq $Verbosity
}

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
        Write-Host "suite - ${Name}: 0 tests ($([int] $Duration.TotalMilliseconds)ms)"
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
        Write-Host "suite - ${Name}: $passedCount passed ($([int] $Duration.TotalMilliseconds)ms)"
        return
    }

    Write-Host "suite - ${Name}: $passedCount/$($suiteResults.Count) passed ($([int] $Duration.TotalMilliseconds)ms)"
}

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
        }

        return "at ${scriptPath}:$($Matches["line"])"
    }

    $invocation = $ErrorRecord.InvocationInfo
    if ($null -ne $invocation -and -not [string]::IsNullOrWhiteSpace($invocation.ScriptName)) {
        return "at $($invocation.ScriptName):$($invocation.ScriptLineNumber)"
    }

    ""
}

function Step {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    if (VerbosityIs "Detailed") {
        Write-Host "step - $Name"
    }

    try {
        & $Body
        $stopwatch.Stop()

        if (VerbosityIs "Detailed") {
            Write-Host "ok - step - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        }
    }
    catch {
        $stopwatch.Stop()

        if (VerbosityIs "Detailed") {
            Write-Host "not ok - step - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        }

        $location = FormatStepFailureLocation $_
        $message = "step: $Name"
        if (-not [string]::IsNullOrWhiteSpace($location)) {
            $message = "$message`n$location"
        }

        throw "$message`n$($_.Exception.Message)"
    }
}

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
        Write-Host "suite - $Name"
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
            Write-Host "ok - $suite - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
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
    Write-Host "not ok - $suite - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
    Write-Host $message
}
