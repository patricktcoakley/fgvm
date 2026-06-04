function InvokeProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FilePath,

        [string[]] $Arguments = @(),

        [hashtable] $Environment = @{},

        [string] $WorkingDirectory = (Get-Location).Path,

        [int] $TimeoutSeconds = 60
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.UseShellExecute = $false

    foreach ($argument in $Arguments) {
        [void] $startInfo.ArgumentList.Add($argument)
    }

    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[$entry.Key] = [string] $entry.Value
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw "Failed to start process: $FilePath"
    }

    try {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()

        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            throw "Process timed out after $TimeoutSeconds seconds: $FilePath $($Arguments -join ' ')"
        }

        $process.WaitForExit()

        [pscustomobject]@{
            FilePath = $FilePath
            Arguments = $Arguments
            ExitCode = $process.ExitCode
            Stdout = $stdoutTask.GetAwaiter().GetResult()
            Stderr = $stderrTask.GetAwaiter().GetResult()
        }
    }
    finally {
        $process.Dispose()
    }
}

function Run {
    param(
        [Parameter(ValueFromRemainingArguments = $true, Position = 0)]
        [string[]] $Arguments,

        [string] $Cwd = ""
    )

    if ($null -eq $script:CurrentContext) {
        throw "Run can only be used inside Test."
    }

    $workingDirectory = if ([string]::IsNullOrWhiteSpace($Cwd)) {
        $script:Config.RepoRoot
    }
    else {
        $Cwd
    }

    $environment = @{
        FGVM_HOME = $script:CurrentContext.HomePath
    }

    if (-not [string]::IsNullOrWhiteSpace($script:CurrentContext.FixtureManifestPath)) {
        $environment["FGVM_INTEGRATION_FIXTURE_MANIFEST"] = $script:CurrentContext.FixtureManifestPath
    }

    InvokeProcess `
        -FilePath $script:CurrentContext.FgvmPath `
        -Arguments $Arguments `
        -Environment $environment `
        -WorkingDirectory $workingDirectory `
        -TimeoutSeconds $script:Config.TimeoutSeconds
}
