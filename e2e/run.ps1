param(
    [string] $FgvmPath = "",
    [string] $TestsPath = "",
    [int] $TimeoutSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptRoot
$modulePath = Join-Path $scriptRoot "harness" "Fgvm.E2E.psm1"

Import-Module -Name $modulePath -Force

if ([string]::IsNullOrWhiteSpace($FgvmPath)) {
    $FgvmPath = Fgvm.ResolvePath $repoRoot
}

if ([string]::IsNullOrWhiteSpace($TestsPath)) {
    $TestsPath = Join-Path $scriptRoot "tests"
}

if (-not [System.IO.File]::Exists($FgvmPath)) {
    throw "fgvm executable was not found: $FgvmPath. Run `mise run e2e:prepare` first."
}

$testFiles = @(Get-ChildItem -LiteralPath $TestsPath -Filter "*.ps1" -File -Recurse | Sort-Object -Property FullName)
if ($testFiles.Count -eq 0) {
    throw "No e2e tests found in $TestsPath."
}

Fgvm.Initialize $FgvmPath $TimeoutSeconds

foreach ($testFile in $testFiles) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        & $testFile.FullName
    }
    catch {
        $stopwatch.Stop()
        $suiteName = [System.IO.Path]::GetFileNameWithoutExtension($testFile.Name)
        Fgvm.AddResult $suiteName "load" $false $stopwatch.Elapsed $_.Exception.Message
        Write-Host "not ok - $suiteName - load ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        Write-Host $_.Exception.Message
    }
}

$results = @(Fgvm.Results)
$failures = @($results | Where-Object { -not $_.Passed })

if ($results.Count -eq 0) {
    throw "No e2e tests were registered by files in $TestsPath."
}

if ($failures.Count -gt 0) {
    $passedCount = $results.Count - $failures.Count
    Write-Host "$passedCount/$($results.Count) e2e test(s) passed."
    throw "$($failures.Count) e2e test(s) failed."
}

Write-Host "$($results.Count) e2e test(s) passed."
