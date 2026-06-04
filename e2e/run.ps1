[CmdletBinding(DefaultParameterSetName = "Directory")]
param(
    [string] $FgvmPath = "",

    [Parameter(ParameterSetName = "Directory")]
    [string] $TestsPath = "",

    [Parameter(Mandatory = $true, ParameterSetName = "File")]
    [string] $TestPath,

    [Parameter(ParameterSetName = "Directory")]
    [switch] $Parallel,

    [Parameter(ParameterSetName = "Directory")]
    [ValidateRange(1, 64)]
    [int] $MaxParallel = [System.Math]::Max(1, [System.Environment]::ProcessorCount),

    [string] $FixtureManifestPath = "",
    [int] $TimeoutSeconds = 60,
    [ValidateSet("Quiet", "Normal", "Detailed")]
    [string] $Verbosity = "Normal",
    [string] $ResultsPath = "",
    [switch] $NoSummary
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptRoot
$modulePath = Join-Path $scriptRoot "lib" "E2E.psm1"

Import-Module -Name $modulePath -Force

function GetPowerShellPath {
    $path = [System.Environment]::ProcessPath
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        return $path
    }

    (Get-Process -Id $PID).Path
}

function NewSyntheticResult {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [System.IO.FileInfo] $TestFile,

        [Parameter(Mandatory = $true, Position = 1)]
        [timespan] $Duration,

        [Parameter(Mandatory = $true, Position = 2)]
        [string] $Message
    )

    [pscustomobject]@{
        Suite = [System.IO.Path]::GetFileNameWithoutExtension($TestFile.Name)
        Name = "process"
        Passed = $false
        DurationMilliseconds = [int] $Duration.TotalMilliseconds
        Message = $Message
    }
}

function WriteResultsFile {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [object[]] $Results,

        [Parameter(Position = 1)]
        [string] $Path = ""
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return
    }

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $serializable = @(
        foreach ($result in $Results) {
            [pscustomobject]@{
                Suite = $result.Suite
                Name = $result.Name
                Passed = $result.Passed
                DurationMilliseconds = [int] $result.Duration.TotalMilliseconds
                Message = $result.Message
            }
        }
    )

    ConvertTo-Json -InputObject $serializable -Depth 5 | Set-Content -LiteralPath $Path -NoNewline
}

function ReadResultsFile {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [System.IO.FileInfo] $TestFile,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Path,

        [Parameter(Mandatory = $true, Position = 2)]
        [timespan] $Duration
    )

    if (-not [System.IO.File]::Exists($Path)) {
        return @(NewSyntheticResult $TestFile $Duration "Suite process did not write results.")
    }

    try {
        return @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    }
    catch {
        return @(NewSyntheticResult $TestFile $Duration "Suite process wrote invalid results: $($_.Exception.Message)")
    }
}

function StartSuiteProcess {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [System.IO.FileInfo] $TestFile,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $PowerShellPath,

        [Parameter(Mandatory = $true, Position = 2)]
        [string] $ResultPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $PowerShellPath
    $startInfo.WorkingDirectory = $repoRoot
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    foreach ($argument in @(
            "-NoLogo",
            "-NoProfile",
            "-File",
            $PSCommandPath,
            "-TestPath",
            $TestFile.FullName,
            "-FgvmPath",
            $FgvmPath,
            "-FixtureManifestPath",
            $FixtureManifestPath,
            "-TimeoutSeconds",
            [string] $TimeoutSeconds,
            "-Verbosity",
            $Verbosity,
            "-ResultsPath",
            $ResultPath,
            "-NoSummary"
        )) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start suite process for $($TestFile.FullName)."
    }

    [pscustomobject]@{
        TestFile = $TestFile
        ResultPath = $ResultPath
        Process = $process
        StdoutTask = $process.StandardOutput.ReadToEndAsync()
        StderrTask = $process.StandardError.ReadToEndAsync()
        Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    }
}

function CompleteSuiteProcess {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [pscustomobject] $Worker
    )

    $Worker.Process.WaitForExit()
    $Worker.Stopwatch.Stop()

    $stdout = $Worker.StdoutTask.GetAwaiter().GetResult()
    $stderr = $Worker.StderrTask.GetAwaiter().GetResult()

    if (-not [string]::IsNullOrWhiteSpace($stdout)) {
        Write-Host $stdout.TrimEnd()
    }

    if (-not [string]::IsNullOrWhiteSpace($stderr)) {
        Write-Host $stderr.TrimEnd()
    }

    $results = @(ReadResultsFile $Worker.TestFile $Worker.ResultPath $Worker.Stopwatch.Elapsed)
    $failedResults = @($results | Where-Object { -not $_.Passed })
    if ($Worker.Process.ExitCode -ne 0 -and $failedResults.Count -eq 0) {
        $message = "Suite process exited with code $($Worker.Process.ExitCode)."
        $results += NewSyntheticResult $Worker.TestFile $Worker.Stopwatch.Elapsed $message
    }

    $Worker.Process.Dispose()
    $results
}

function RunSuitesInParallel {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [System.IO.FileInfo[]] $TestFiles
    )

    $pending = [System.Collections.Generic.Queue[System.IO.FileInfo]]::new()
    foreach ($testFile in $TestFiles) {
        $pending.Enqueue($testFile)
    }

    $running = [System.Collections.Generic.List[object]]::new()
    $allResults = [System.Collections.Generic.List[object]]::new()
    $powerShellPath = GetPowerShellPath
    $resultRoot = Join-Path ([System.IO.Path]::GetTempPath()) "fgvm-e2e-results-$([System.Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null

    try {
        while ($pending.Count -gt 0 -or $running.Count -gt 0) {
            while ($pending.Count -gt 0 -and $running.Count -lt $MaxParallel) {
                $testFile = $pending.Dequeue()
                $safeName = [System.IO.Path]::GetFileNameWithoutExtension($testFile.Name) -replace '[^a-zA-Z0-9._-]', '-'
                $resultPath = Join-Path $resultRoot "$safeName-$([System.Guid]::NewGuid().ToString('N')).json"
                [void] $running.Add((StartSuiteProcess $testFile $powerShellPath $resultPath))
            }

            $finished = @($running | Where-Object { $_.Process.HasExited })
            if ($finished.Count -eq 0) {
                Start-Sleep -Milliseconds 50
                continue
            }

            foreach ($worker in $finished) {
                foreach ($result in @(CompleteSuiteProcess $worker)) {
                    [void] $allResults.Add($result)
                }

                [void] $running.Remove($worker)
            }
        }
    }
    finally {
        foreach ($worker in $running) {
            if (-not $worker.Process.HasExited) {
                $worker.Process.Kill($true)
            }

            $worker.Process.Dispose()
        }

        if (Test-Path -LiteralPath $resultRoot) {
            Remove-Item -LiteralPath $resultRoot -Recurse -Force
        }
    }

    $results = @($allResults)
    if ($results.Count -eq 0) {
        throw "No e2e tests were registered by files in $testSource."
    }

    $failures = @($results | Where-Object { -not $_.Passed })
    if ($failures.Count -gt 0) {
        $passedCount = $results.Count - $failures.Count
        Write-Host "$passedCount/$($results.Count) e2e test(s) passed."
        throw "$($failures.Count) e2e test(s) failed."
    }

    Write-Host "$($results.Count) e2e test(s) passed."
}

if ([string]::IsNullOrWhiteSpace($FgvmPath)) {
    $FgvmPath = ResolveCliPath $repoRoot
}

if ($PSCmdlet.ParameterSetName -eq "Directory" -and [string]::IsNullOrWhiteSpace($TestsPath)) {
    $TestsPath = Join-Path $scriptRoot "tests"
}

if ([string]::IsNullOrWhiteSpace($FixtureManifestPath)) {
    $FixtureManifestPath = ResolveFixtureManifestPath $repoRoot
}

if (-not [System.IO.File]::Exists($FgvmPath)) {
    throw "fgvm executable was not found: $FgvmPath. Run `mise run e2e:prepare` first."
}

if (-not [System.IO.File]::Exists($FixtureManifestPath)) {
    throw "e2e fixture manifest was not found: $FixtureManifestPath. Run `mise run e2e:prepare` first."
}

if ($PSCmdlet.ParameterSetName -eq "File") {
    $testFile = Get-Item -LiteralPath $TestPath -ErrorAction Stop
    if ($testFile.PSIsContainer) {
        throw "TestPath must be a PowerShell test file: $TestPath"
    }

    $testFiles = @($testFile)
}
else {
    $testFiles = @(Get-ChildItem -LiteralPath $TestsPath -Filter "*.ps1" -File -Recurse | Sort-Object -Property FullName)
}

$testSource = if ($PSCmdlet.ParameterSetName -eq "File") {
    $TestPath
}
else {
    $TestsPath
}

if ($testFiles.Count -eq 0) {
    throw "No e2e tests found in $testSource."
}

if ($Parallel -and $testFiles.Count -gt 1) {
    RunSuitesInParallel $testFiles
    return
}

Initialize $FgvmPath $TimeoutSeconds $FixtureManifestPath $repoRoot $Verbosity

foreach ($testFile in $testFiles) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        & $testFile.FullName
    }
    catch {
        $stopwatch.Stop()
        $suiteName = [System.IO.Path]::GetFileNameWithoutExtension($testFile.Name)
        AddResult $suiteName "load" $false $stopwatch.Elapsed $_.Exception.Message
        Write-Host "not ok - $suiteName - load ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        Write-Host $_.Exception.Message
    }
}

$results = @(Results)
$failures = @($results | Where-Object { -not $_.Passed })

WriteResultsFile $results $ResultsPath

if ($results.Count -eq 0) {
    throw "No e2e tests were registered by files in $testSource."
}

if ($failures.Count -gt 0) {
    $passedCount = $results.Count - $failures.Count
    if (-not $NoSummary) {
        Write-Host "$passedCount/$($results.Count) e2e test(s) passed."
    }

    throw "$($failures.Count) e2e test(s) failed."
}

if (-not $NoSummary) {
    Write-Host "$($results.Count) e2e test(s) passed."
}
