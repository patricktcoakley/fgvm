<#
.SYNOPSIS
    Represents an isolated test environment with a unique temp directory and
    all paths needed by the fgvm CLI (FGVM_HOME, bin, shim, releases, etc.).
#>
class E2EContext {
    [string] $Name
    [string] $RootPath
    [string] $HomePath
    [string] $WorkPath
    [string] $FgvmRootPath
    [string] $ReleasesPath
    [string] $InstallationsPath
    [string] $InstallationsDirectoryPath
    [string] $BinPath
    [string] $ShimPath
    [string] $SelectedArtifactPath
    [string] $LogPath
    [string] $FgvmPath
    [string] $FixtureManifestPath

    E2EContext([string] $name, [string] $rootPath, [string] $fgvmPath, [string] $fixtureManifestPath) {
        $this.Name = $name
        $this.RootPath = $rootPath
        $this.HomePath = Join-Path $rootPath "home"
        $this.WorkPath = Join-Path $rootPath "work"
        $this.FgvmRootPath = $this.HomePath
        $this.ReleasesPath = Join-Path $this.FgvmRootPath "releases.json"
        $this.InstallationsPath = Join-Path $this.FgvmRootPath "installations.json"
        $this.InstallationsDirectoryPath = Join-Path $this.FgvmRootPath "installations"
        $this.BinPath = Join-Path $this.FgvmRootPath "bin"
        $this.ShimPath = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
            Join-Path $this.BinPath "godot.cmd"
        }
        else {
            Join-Path $this.BinPath "godot"
        }
        $this.SelectedArtifactPath = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
            Join-Path $this.FgvmRootPath "Godot.url"
        }
        elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
            Join-Path $this.FgvmRootPath "Godot.app"
        }
        else {
            Join-Path $this.FgvmRootPath "Godot"
        }
        $this.LogPath = Join-Path $this.FgvmRootPath "fgvm.log"
        $this.FgvmPath = $fgvmPath
        $this.FixtureManifestPath = $fixtureManifestPath
    }
}

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:CurrentSuite = ""
$script:CurrentContext = $null
$script:Config = $null

<#
.SYNOPSIS
    Reset and configure the e2e test run.
.PARAMETER FgvmPath
    Path to the fgvm CLI executable to test.
.PARAMETER TimeoutSeconds
    Per-process timeout for fgvm invocations (default 60).
.PARAMETER FixtureManifestPath
    Path to the fixture manifest JSON file.
.PARAMETER RepoRoot
    Repository root used as the default working directory (default: current dir).
.PARAMETER Verbosity
    Output verbosity: Quiet, Normal, or Detailed.
#>
function Initialize {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $FgvmPath,

        [Parameter(Position = 1)]
        [int] $TimeoutSeconds = 60,

        [Parameter(Position = 2)]
        [string] $FixtureManifestPath = "",

        [Parameter(Position = 3)]
        [string] $RepoRoot = (Get-Location).Path,

        [Parameter(Position = 4)]
        [ValidateSet("Quiet", "Normal", "Detailed")]
        [string] $Verbosity = "Normal"
    )

    $script:Results.Clear()
    $script:CurrentSuite = ""
    $script:CurrentContext = $null
    $script:Config = [pscustomobject]@{
        FgvmPath            = $FgvmPath
        TimeoutSeconds      = $TimeoutSeconds
        FixtureManifestPath = $FixtureManifestPath
        RepoRoot            = $RepoRoot
        Verbosity           = $Verbosity
    }
}

<#
.SYNOPSIS
    Record a test result.
.PARAMETER Suite
    Suite name the result belongs to.
.PARAMETER Name
    Test name.
.PARAMETER Passed
    Whether the test passed.
.PARAMETER Duration
    How long the test took.
.PARAMETER Message
    Optional failure message.
#>
function AddResult {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Suite,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 2)]
        [bool] $Passed,

        [Parameter(Mandatory = $true, Position = 3)]
        [timespan] $Duration,

        [Parameter(Position = 4)]
        [string] $Message = ""
    )

    [void] $script:Results.Add([pscustomobject]@{
            Suite    = $Suite
            Name     = $Name
            Passed   = $Passed
            Duration = $Duration
            Message  = $Message
        })
}

<#
.SYNOPSIS
    Yield all recorded test results.
#>
function Results {
    foreach ($result in $script:Results) {
        $result
    }
}

<#
.SYNOPSIS
    Detect the current OS-architecture platform string.
.DESCRIPTION
    Returns a string like "macos-arm64", "linux-x64", or "windows-x64" by
    inspecting the runtime OS and process architecture.
#>
function Platform {
    $os = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        "windows"
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
        "macos"
    }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) {
        "linux"
    }
    else {
        throw "Unsupported operating system."
    }

    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture) {
        "X64" { "x64" }
        "Arm64" { "arm64" }
        default { throw "Unsupported architecture: $([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture)" }
    }

    "$os-$arch"
}

<#
.SYNOPSIS
    Resolve the path to the prebuilt fgvm CLI binary for the current platform.
.PARAMETER RepoRoot
    Repository root directory.
#>
function ResolveCliPath {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $RepoRoot
    )

    $platform = Platform
    $executable = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        "fgvm.exe"
    }
    else {
        "fgvm"
    }

    Join-Path $RepoRoot "e2e" ".cli" $platform $executable
}

<#
.SYNOPSIS
    Resolve the path to the fixture manifest JSON for the current platform.
.PARAMETER RepoRoot
    Repository root directory.
#>
function ResolveFixtureManifestPath {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $RepoRoot
    )

    Join-Path $RepoRoot "e2e" ".fixtures" (Platform) "manifest.json"
}

<#
.SYNOPSIS
    Create a new temporary test context with an isolated FGVM_HOME.
.DESCRIPTION
    Creates a unique temp directory, initialises the FGVM_HOME and work
    directories, and returns an E2EContext describing the environment.
.PARAMETER Name
    Human-readable name for the context (used in the temp dir name).
.RETURNS
    E2EContext with all paths configured.
#>
function NewContext {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name
    )

    if ($null -eq $script:Config) {
        throw "The e2e run has not been initialized."
    }

    $safeName = $Name -replace '[^a-zA-Z0-9._-]', '-'
    $rootPath = Join-Path ([System.IO.Path]::GetTempPath()) "fgvm-e2e-$safeName-$([System.Guid]::NewGuid().ToString('N'))"
    $context = [E2EContext]::new($Name, $rootPath, $script:Config.FgvmPath, $script:Config.FixtureManifestPath)

    New-Item -ItemType Directory -Path $context.HomePath -Force | Out-Null
    New-Item -ItemType Directory -Path $context.WorkPath -Force | Out-Null

    $context
}

<#
.SYNOPSIS
    Clean up a test context's temporary directory.
.PARAMETER Context
    The E2EContext to remove.
#>
function RemoveContext {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [E2EContext] $Context
    )

    if (Test-Path -LiteralPath $Context.RootPath) {
        Remove-Item -LiteralPath $Context.RootPath -Recurse -Force
    }
}
