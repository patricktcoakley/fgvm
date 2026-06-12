<#
.SYNOPSIS
    Run an external process with timeout and capture stdout, stderr, and exit code.
.PARAMETER FilePath
    Path to the executable to run.
.PARAMETER Arguments
    List of argument strings passed to the process.
.PARAMETER Environment
    Hashtable of environment variable overrides for the child process.
.PARAMETER WorkingDirectory
    Working directory for the child process (default: current directory).
.PARAMETER TimeoutSeconds
    Maximum time to wait for the process to exit (default 60).
.RETURNS
    pscustomobject with FilePath, Arguments, ExitCode (int), Stdout (string), Stderr (string).
#>
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

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()

        $stdout = ($stdout -replace "\e\[[0-9;]*m", "").Replace('\', '/')
        $stderr = ($stderr -replace "\e\[[0-9;]*m", "").Replace('\', '/')

        [pscustomobject]@{
            FilePath  = $FilePath
            Arguments = $Arguments
            ExitCode  = $process.ExitCode
            Stdout    = $stdout
            Stderr    = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

<#
.SYNOPSIS
    Build the environment variable block for fgvm process invocations.
.DESCRIPTION
    Sets FGVM_HOME and FGVM_INTEGRATION_FIXTURE_MANIFEST from the current
    test context, then applies any caller-supplied overrides on top.
.PARAMETER Overrides
    Optional hashtable of additional environment variable overrides.
#>
function Get-E2EProcessEnvironment {
    param(
        [hashtable] $Overrides = @{}
    )

    $processEnvironment = @{
        FGVM_HOME = $script:CurrentContext.HomePath
    }

    if (-not [string]::IsNullOrWhiteSpace($script:CurrentContext.FixtureManifestPath)) {
        $processEnvironment["FGVM_INTEGRATION_FIXTURE_MANIFEST"] = $script:CurrentContext.FixtureManifestPath
    }

    foreach ($entry in $Overrides.GetEnumerator()) {
        $processEnvironment[$entry.Key] = $entry.Value
    }

    $processEnvironment
}

<#
.SYNOPSIS
    Wait for a background process to exit by PID.
.PARAMETER Id
    Process ID to wait for.
.PARAMETER TimeoutSeconds
    Maximum time to wait (default 5).
#>
function Process.WaitForExit {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [int] $Id,

        [Parameter(Position = 1)]
        [int] $TimeoutSeconds = 5
    )

    try {
        $process = [System.Diagnostics.Process]::GetProcessById($Id)
    }
    catch [System.ArgumentException] {
        return
    }

    try {
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            throw "Process $Id did not exit within $TimeoutSeconds seconds."
        }
    }
    finally {
        $process.Dispose()
    }
}

<#
.SYNOPSIS
    Run the fgvm CLI with arguments in the current test context.
.DESCRIPTION
    Invokes the fgvm binary pointed to by the current E2EContext, with
    FGVM_HOME and fixture manifest set automatically. Prefer this over
    direct InvokeProcess calls inside tests.
.PARAMETER Arguments
    CLI arguments and flags passed to fgvm.
.PARAMETER Cwd
    Working directory override (defaults to the repo root).
.PARAMETER Environment
    Additional environment variable overrides.
#>
function Run {
    param(
        [Parameter(ValueFromRemainingArguments = $true, Position = 0)]
        [string[]] $Arguments,

        [string] $Cwd = "",

        [hashtable] $Environment = @{}
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

    $processEnvironment = Get-E2EProcessEnvironment $Environment

    InvokeProcess `
        -FilePath $script:CurrentContext.FgvmPath `
        -Arguments $Arguments `
        -Environment $processEnvironment `
        -WorkingDirectory $workingDirectory `
        -TimeoutSeconds $script:Config.TimeoutSeconds
}

<#
.SYNOPSIS
    Invoke the Godot PATH shim through the test environment.
.DESCRIPTION
    Runs the godot shim (via the system PATH with FGVM_HOME/bin prepended)
    so that shim resolution and argument forwarding can be tested end-to-end.
    Prefer this over direct InvokeProcess calls inside tests.
.PARAMETER Arguments
    Arguments forwarded through the shim to the mock Godot binary.
.PARAMETER Environment
    Additional environment variable overrides.
#>
function Invoke-GodotShim {
    param(
        [Parameter(ValueFromRemainingArguments = $true, Position = 0)]
        [string[]] $Arguments,

        [hashtable] $Environment = @{}
    )

    if ($null -eq $script:CurrentContext) {
        throw "Invoke-GodotShim can only be used inside Test."
    }

    $processEnvironment = Get-E2EProcessEnvironment $Environment
    $currentPath = [System.Environment]::GetEnvironmentVariable("PATH")
    $processEnvironment["PATH"] = if ([string]::IsNullOrWhiteSpace($currentPath)) {
        $script:CurrentContext.BinPath
    }
    else {
        "$($script:CurrentContext.BinPath)$([System.IO.Path]::PathSeparator)$currentPath"
    }

    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $launcher = [System.Environment]::GetEnvironmentVariable("ComSpec")
        if ([string]::IsNullOrWhiteSpace($launcher)) {
            $launcher = "cmd.exe"
        }

        $launcherArguments = @("/d", "/c", "godot") + $Arguments
    }
    else {
        $launcher = "/usr/bin/env"
        $launcherArguments = @("godot") + $Arguments
    }

    InvokeProcess `
        -FilePath $launcher `
        -Arguments $launcherArguments `
        -Environment $processEnvironment `
        -WorkingDirectory $script:Config.RepoRoot `
        -TimeoutSeconds $script:Config.TimeoutSeconds
}

<#
.SYNOPSIS
    Read and parse a mock Godot invocation JSON file, normalizing paths to forward slashes.
.DESCRIPTION
    The mock Godot binary writes BaseDirectory and WorkingDirectory with OS-native
    separators (backslashes on Windows). This helper normalizes them to forward slashes
    so callers can compare against fixture paths (always forward-slash) without
    inline separator replacements.
.PARAMETER Path
    Path to the invocation JSON file written by the mock binary.
#>
function Read-MockInvocation {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    $invocation = Json (File.Read $Path)
    $invocation.BaseDirectory = $invocation.BaseDirectory.Replace('\', '/')
    $invocation.WorkingDirectory = $invocation.WorkingDirectory.Replace('\', '/')
    $invocation
}
