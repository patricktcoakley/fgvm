Set-StrictMode -Version Latest

# A detached replacement should outlive its launcher and leave the launch console.
if (-not $IsWindows) {
    return
}

function Invoke-WindowsConsoleProbe {
    param([ValidateSet("direct", "detached")] [string] $Mode)

    $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
    $markerRoot = Join-Path $Context.WorkPath "console-$Mode"
    $probeScript = Join-Path (Split-Path -Parent $PSScriptRoot) "fixtures/WindowsConsoleProbe.ps1"

    $probe = InvokeProcess `
        -FilePath ([System.Environment]::ProcessPath) `
        -Arguments @(
            "-NoLogo", "-NoProfile", "-File", $probeScript,
            "-FgvmPath", $Context.FgvmPath,
            "-MockGodotPath", $seeded.ExecutablePath,
            "-WorkPath", $Context.WorkPath,
            "-MarkerRoot", $markerRoot,
            "-Mode", $Mode
        ) `
        -Environment @{
            FGVM_HOME = $Context.FgvmRootPath
            FGVM_INTEGRATION_FIXTURE_MANIFEST = $Context.FixtureManifestPath
        } `
        -WorkingDirectory $Context.WorkPath

    Assert.ExitCode 0 $probe "Windows console inheritance probe"
    $result = Json (File.Read "$markerRoot.console.json")
    $survived = Json (File.Read "$markerRoot.survived.json")
    Assert.Equal $result.ProcessId $survived.ProcessId
    $result
}

Suite "Windows console detachment" {
    Test "direct replacement inherits its launch console" {
        $result = Invoke-WindowsConsoleProbe "direct"
        Assert.True $result.AttachedToConsole
    }

    Test "detached replacement leaves its launch console" {
        $result = Invoke-WindowsConsoleProbe "detached"
        Assert.False $result.AttachedToConsole
    }
}
