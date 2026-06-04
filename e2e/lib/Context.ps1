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
    [string] $LogPath
    [string] $FgvmPath
    [string] $FixtureManifestPath

    E2EContext([string] $name, [string] $rootPath, [string] $fgvmPath, [string] $fixtureManifestPath) {
        $this.Name = $name
        $this.RootPath = $rootPath
        $this.HomePath = Join-Path $rootPath "home"
        $this.WorkPath = Join-Path $rootPath "work"
        $this.FgvmRootPath = Join-Path $this.HomePath "fgvm"
        $this.ReleasesPath = Join-Path $this.FgvmRootPath "releases.json"
        $this.InstallationsPath = Join-Path $this.FgvmRootPath "installations.json"
        $this.InstallationsDirectoryPath = Join-Path $this.FgvmRootPath "installations"
        $this.BinPath = Join-Path $this.FgvmRootPath "bin"
        $this.LogPath = Join-Path $this.FgvmRootPath "fgvm.log"
        $this.FgvmPath = $fgvmPath
        $this.FixtureManifestPath = $fixtureManifestPath
    }
}

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:CurrentSuite = ""
$script:CurrentContext = $null
$script:Config = $null

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
        FgvmPath = $FgvmPath
        TimeoutSeconds = $TimeoutSeconds
        FixtureManifestPath = $FixtureManifestPath
        RepoRoot = $RepoRoot
        Verbosity = $Verbosity
    }
}

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
            Suite = $Suite
            Name = $Name
            Passed = $Passed
            Duration = $Duration
            Message = $Message
        })
}

function Results {
    foreach ($result in $script:Results) {
        $result
    }
}

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

    Join-Path $RepoRoot ".fgvm-e2e-cli" $platform $executable
}

function ResolveFixtureManifestPath {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $RepoRoot
    )

    Join-Path $RepoRoot ".fgvm-e2e-fixtures" (Platform) "manifest.json"
}

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

function RemoveContext {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [E2EContext] $Context
    )

    if (Test-Path -LiteralPath $Context.RootPath) {
        Remove-Item -LiteralPath $Context.RootPath -Recurse -Force
    }
}
