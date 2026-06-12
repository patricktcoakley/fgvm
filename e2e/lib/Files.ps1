<#
.SYNOPSIS
    Parse a JSON string into an object, preserving arrays as single items.
.PARAMETER Text
    JSON string to parse.
.RETURNS
    The parsed object. Arrays are returned as a single collection, not unrolled.
#>
function Json {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Text
    )

    try {
        $value = $Text | ConvertFrom-Json -NoEnumerate
    }
    catch {
        throw "Failed to parse JSON. $($_.Exception.Message)`n$Text"
    }

    , $value
}

<#
.SYNOPSIS
    Check whether a file exists on disk.
.PARAMETER Path
    Path to the file.
#>
function File.Exists {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    Test-Path -LiteralPath $Path -PathType Leaf
}

<#
.SYNOPSIS
    Read the entire content of a file as a single string.
.PARAMETER Path
    Path to the file.
#>
function File.Read {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    Get-Content -LiteralPath $Path -Raw
}

<#
.SYNOPSIS
    Get a file's last write timestamp in UTC.
.PARAMETER Path
    Path to the file.
#>
function File.ModifiedAt {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    (Get-Item -LiteralPath $Path).LastWriteTimeUtc
}

<#
.SYNOPSIS
    Set a file's last write timestamp.
.PARAMETER Path
    Path to the file.
.PARAMETER ModifiedAt
    UTC timestamp to assign.
#>
function File.SetModifiedAt {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path,

        [Parameter(Mandatory = $true, Position = 1)]
        [datetime] $ModifiedAt
    )

    (Get-Item -LiteralPath $Path).LastWriteTimeUtc = $ModifiedAt
}

<#
.SYNOPSIS
    Wait for a file to appear, with a timeout.
.PARAMETER Path
    Path to the file to wait for.
.PARAMETER TimeoutSeconds
    Maximum time to wait (default 5).
#>
function File.WaitFor {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path,

        [Parameter(Position = 1)]
        [int] $TimeoutSeconds = 5
    )

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if (Test-Path -LiteralPath $Path -PathType Leaf) {
            return
        }

        Start-Sleep -Milliseconds 25
    }

    throw "Timed out waiting for file: $Path"
}

<#
.SYNOPSIS
    Parse a JSON manifest file into a hashtable.
.PARAMETER Path
    Path to the JSON manifest file.
#>
function Manifest.From {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path
    )

    try {
        File.Read $Path | ConvertFrom-Json -AsHashtable
    }
    catch {
        throw "Failed to parse manifest at $Path. $($_.Exception.Message)"
    }
}

<#
.SYNOPSIS
    Write a manifest object to a JSON file (compressed, with optional timestamp).
.PARAMETER Path
    Output file path.
.PARAMETER Manifest
    The manifest object (hashtable or pscustomobject) to serialise.
.PARAMETER ModifiedAt
    Optional UTC timestamp to set on the written file.
#>
function Manifest.Write {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path,

        [Parameter(Mandatory = $true, Position = 1)]
        [object] $Manifest,

        [Parameter(Position = 2)]
        [Nullable[datetime]] $ModifiedAt = $null
    )

    New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
    Set-Content -LiteralPath $Path -Value ($Manifest | ConvertTo-Json -Depth 10 -Compress) -NoNewline

    if ($null -ne $ModifiedAt) {
        File.SetModifiedAt $Path $ModifiedAt
    }
}

<#
.SYNOPSIS
    Get the default fixture target filename for a platform and runtime.
.PARAMETER FixturePlatform
    OS-architecture string (e.g. "macos-arm64", "linux-x64").
.PARAMETER Runtime
    Runtime variant: "standard" or "mono".
#>
function Get-DefaultFixtureTarget {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $FixturePlatform,

        [Parameter(Mandatory = $true, Position = 1)]
        [ValidateSet("standard", "mono")]
        [string] $Runtime
    )

    switch ($FixturePlatform.ToLowerInvariant()) {
        "linux-x64" {
            if ($Runtime -eq "mono") { return "mono_linux_x86_64" }
            return "linux.x86_64"
        }
        "linux-arm64" {
            if ($Runtime -eq "mono") { return "mono_linux_arm64" }
            return "linux.arm64"
        }
        "macos-x64" {
            if ($Runtime -eq "mono") { return "mono_macos.universal" }
            return "macos.universal"
        }
        "macos-arm64" {
            if ($Runtime -eq "mono") { return "mono_macos.universal" }
            return "macos.universal"
        }
        "windows-x64" {
            if ($Runtime -eq "mono") { return "mono_win64" }
            return "win64.exe"
        }
        "windows-arm64" {
            if ($Runtime -eq "mono") { return "mono_windows_arm64" }
            return "windows_arm64.exe"
        }
        default {
            throw "Unsupported fixture platform: $FixturePlatform"
        }
    }
}

<#
.SYNOPSIS
    Seed a fixture installation into the test environment.
.DESCRIPTION
    Extracts a fixture zip into the FGVM_HOME/installations directory, updates
    the installations registry, and returns an object describing the installed
    version's paths.
.PARAMETER Release
    Release name from the fixture manifest (e.g. "4.6.2-stable").
.PARAMETER Runtime
    Runtime variant: "standard" (default) or "mono".
.PARAMETER Target
    Target filename override (auto-detected from platform if omitted).
.PARAMETER Default
    If set, mark this installation as the default version.
.RETURNS
    pscustomobject with Release, Runtime, Target, Name (release-runtime),
    Key (release-runtime@target), RelativePath, InstallationPath,
    ExecutablePath, and ShortcutTargetPath.
#>
function Add-FixtureInstallation {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Release,

        [Parameter(Position = 1)]
        [ValidateSet("standard", "mono")]
        [string] $Runtime = "standard",

        [string] $Target = "",

        [switch] $Default
    )

    if ($null -eq $script:CurrentContext) {
        throw "Add-FixtureInstallation can only be used inside Test."
    }

    $manifest = Manifest.From $script:CurrentContext.FixtureManifestPath
    if ([string]::IsNullOrWhiteSpace($Target)) {
        $Target = Get-DefaultFixtureTarget $manifest["platform"] $Runtime
    }

    $artifact = @(
        $manifest["artifacts"] | Where-Object {
            $_["releaseName"] -eq $Release -and
            $_["runtime"] -eq $Runtime -and
            $_["target"] -eq $Target
        }
    )

    if ($artifact.Count -ne 1) {
        throw "Expected one fixture artifact for $Release $Runtime $Target, found $($artifact.Count)."
    }

    $artifact = $artifact[0]
    $releaseNameWithRuntime = "$Release-$Runtime"
    $relativePath = "installations/$releaseNameWithRuntime/$Target"
    $installationPath = Join-Path $script:CurrentContext.FgvmRootPath ($relativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    $manifestRoot = Split-Path -Parent $script:CurrentContext.FixtureManifestPath
    $zipPath = Join-Path $manifestRoot ($artifact["zipPath"] -replace '/', [System.IO.Path]::DirectorySeparatorChar)

    New-Item -ItemType Directory -Path $installationPath -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $installationPath, $true)

    $executablePath = Join-Path $installationPath ($artifact["executablePath"] -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath $executablePath -PathType Leaf)) {
        throw "Seeded fixture executable was not found: $executablePath"
    }

    if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $mode = [System.IO.UnixFileMode]::UserRead `
            -bor [System.IO.UnixFileMode]::UserWrite `
            -bor [System.IO.UnixFileMode]::UserExecute `
            -bor [System.IO.UnixFileMode]::GroupRead `
            -bor [System.IO.UnixFileMode]::GroupExecute `
            -bor [System.IO.UnixFileMode]::OtherRead `
            -bor [System.IO.UnixFileMode]::OtherExecute
        [System.IO.File]::SetUnixFileMode($executablePath, $mode)
    }

    $shortcutRelativePath = if ($artifact["executablePath"].Contains("/Contents/MacOS/", [System.StringComparison]::Ordinal)) {
        $artifact["executablePath"].Split("/Contents/MacOS/", [System.StringSplitOptions]::None)[0]
    }
    else {
        $artifact["executablePath"]
    }
    $shortcutTargetPath = Join-Path $installationPath ($shortcutRelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)

    $registry = if (File.Exists $script:CurrentContext.InstallationsPath) {
        Manifest.From $script:CurrentContext.InstallationsPath
    }
    else {
        [ordered]@{
            default       = $null
            installations = [ordered]@{}
        }
    }

    $key = "$releaseNameWithRuntime@$Target"
    $registry["installations"][$key] = [ordered]@{
        path           = $relativePath
        installedAt    = [datetimeoffset]::UtcNow.ToString("O")
        lastLaunchedAt = $null
    }

    if ($Default) {
        $registry["default"] = $key
    }

    Manifest.Write $script:CurrentContext.InstallationsPath $registry

    [pscustomobject]@{
        Release            = $Release
        Runtime            = $Runtime
        Target             = $Target
        Name               = $releaseNameWithRuntime
        Key                = $key
        RelativePath       = $relativePath
        InstallationPath   = $installationPath
        ExecutablePath     = $executablePath
        ShortcutTargetPath = $shortcutTargetPath
    }
}
