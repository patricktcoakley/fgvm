# Check whether a replacement remains attached to the launch console.
# Direct launches establish normal inheritance; detached launches should leave it.
# A release marker keeps the replacement alive until the console check finishes.
param(
    [Parameter(Mandatory = $true)] [string] $FgvmPath,
    [Parameter(Mandatory = $true)] [string] $MockGodotPath,
    [Parameter(Mandatory = $true)] [string] $WorkPath,
    [Parameter(Mandatory = $true)] [string] $MarkerRoot,
    [Parameter(Mandatory = $true)] [ValidateSet("direct", "detached")] [string] $Mode
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -Path (Join-Path $PSScriptRoot "WindowsConsoleProbe.cs")

function New-TestConsole {
    [void] [WindowsConsoleApi]::FreeConsole()
    if (-not [WindowsConsoleApi]::AllocConsole()) {
        $errorCode = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw [System.ComponentModel.Win32Exception]::new($errorCode, "Could not create the test console.")
    }
}

# The console API reports the required size when the process list does not fit.
function Test-ConsoleContains([int] $ProcessId) {
    $processIds = [uint[]]::new(16)
    $count = [WindowsConsoleApi]::GetConsoleProcessList($processIds, [uint] $processIds.Length)
    if ($count -gt $processIds.Length) {
        $processIds = [uint[]]::new([int] $count)
        $count = [WindowsConsoleApi]::GetConsoleProcessList($processIds, [uint] $processIds.Length)
    }

    if ($count -eq 0 -or $count -gt $processIds.Length) {
        $errorCode = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        throw [System.ComponentModel.Win32Exception]::new($errorCode, "Could not read the test console's processes.")
    }

    for ($index = 0; $index -lt $count; $index++) {
        if ($processIds[$index] -eq $ProcessId) {
            return $true
        }
    }

    $false
}

function Wait-ForFile([string] $Path) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not [System.IO.File]::Exists($Path)) {
        if ($stopwatch.Elapsed.TotalSeconds -ge 15) {
            throw "Timed out waiting for $Path."
        }

        Start-Sleep -Milliseconds 25
    }
}

$initial = $null
New-TestConsole
try {
    $executable = if ($Mode -eq "direct") { $MockGodotPath } else { $FgvmPath }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = $WorkPath
    $startInfo.Environment["FGVM_MOCK_REPLACEMENT_RELEASE"] = "$MarkerRoot.release"
    if ($Mode -eq "detached") {
        [void] $startInfo.ArgumentList.Add("godot")
        [void] $startInfo.ArgumentList.Add("--args")
        [void] $startInfo.ArgumentList.Add("--fgvm-mock-relaunch `"$MarkerRoot`"")
    }
    else {
        [void] $startInfo.ArgumentList.Add("--fgvm-mock-relaunch")
        [void] $startInfo.ArgumentList.Add($MarkerRoot)
    }

    $initial = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $initial) {
        throw "Could not start the initial process."
    }

    Wait-ForFile "$MarkerRoot.started.json"
    $replacement = Get-Content -LiteralPath "$MarkerRoot.started.json" -Raw | ConvertFrom-Json
    $attachedToConsole = Test-ConsoleContains ([int] $replacement.ProcessId)
    @{ ProcessId = $replacement.ProcessId; AttachedToConsole = $attachedToConsole } |
        ConvertTo-Json -Compress |
        Set-Content -LiteralPath "$MarkerRoot.console.json" -NoNewline

    Set-Content -LiteralPath "$MarkerRoot.release" -Value "" -NoNewline
    Wait-ForFile "$MarkerRoot.survived.json"
    if (-not $initial.WaitForExit(15000) -or $initial.ExitCode -ne 0) {
        throw "Initial launch did not exit successfully."
    }
}
finally {
    if ($null -ne $initial) {
        if (-not $initial.HasExited) {
            $initial.Kill($true)
        }

        $initial.Dispose()
    }

    [void] [WindowsConsoleApi]::FreeConsole()
}
