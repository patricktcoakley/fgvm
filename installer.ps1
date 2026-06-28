<#
.SYNOPSIS
    Installs fgvm into the user's fgvm bin directory.

.DESCRIPTION
    Downloads the latest Windows release archive, verifies its SHA-256 checksum,
    installs fgvm.exe into $env:FGVM_INSTALL_DIR or $HOME\fgvm\bin, and adds
    both that directory and $HOME\fgvm\bin to the user PATH. Re-run this script
    to upgrade fgvm.

.PARAMETER Version
    Release version to install. Defaults to latest. Accepts values with or
    without the leading v, such as v2.2.0 or 2.2.0. Version overrides only
    support v2.2.0 or later.

.PARAMETER InstallDir
    Directory for the fgvm binary. Defaults to $env:FGVM_INSTALL_DIR or
    the selected fgvm home's bin directory.

.PARAMETER FgvmHome
    Runtime home for fgvm. Defaults to $env:FGVM_HOME or $HOME\fgvm.

.PARAMETER NoModifyPath
    Install fgvm without changing the user PATH.

.PARAMETER Quiet
    Suppress informational output.

.LINK
    https://github.com/patricktcoakley/fgvm

.EXAMPLE
    irm https://raw.githubusercontent.com/patricktcoakley/fgvm/main/installer.ps1 | iex

.NOTES
    Platform: Windows x64.
    Requires PowerShell 5.0 or later.
#>

[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()]
    [string]$Version = "latest",

    [ValidateNotNullOrEmpty()]
    [string]$FgvmHome = $(if ($env:FGVM_HOME) {
            $env:FGVM_HOME
        }
        else {
            Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)) "fgvm"
        }),

    [ValidateNotNullOrEmpty()]
    [string]$InstallDir = $(if ($env:FGVM_INSTALL_DIR) {
            $env:FGVM_INSTALL_DIR
        }
        else {
            Join-Path $FgvmHome "bin"
        }),

    [switch]$NoModifyPath,

    [switch]$Quiet
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12

$script:Repo = "patricktcoakley/fgvm"
$script:MinimumSupportedVersion = [version]"2.2.0"
$script:RequestedVersion = $Version
$script:DefaultFgvmHome = Join-Path ([System.Environment]::GetFolderPath([System.Environment+SpecialFolder]::UserProfile)) "fgvm"
$script:FgvmHome = [System.IO.Path]::GetFullPath($FgvmHome)
$script:FgvmHomeBin = Join-Path $script:FgvmHome "bin"
$script:InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$script:TargetPath = Join-Path $script:InstallDir "fgvm.exe"
$script:LegacyFgvmupPath = if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA "fgvm" } else { $null }
$script:SkipPathUpdate = [bool]$NoModifyPath
$script:QuietMode = [bool]$Quiet
$script:PersistFgvmHome = -not [string]::Equals(
    $script:FgvmHome,
    [System.IO.Path]::GetFullPath($script:DefaultFgvmHome),
    [System.StringComparison]::OrdinalIgnoreCase)
$script:PersistInstallDir = -not [string]::Equals(
    $script:InstallDir,
    [System.IO.Path]::GetFullPath($script:FgvmHomeBin),
    [System.StringComparison]::OrdinalIgnoreCase)

function Write-Info {
    param([string]$Message)

    if (-not $script:QuietMode) {
        Write-Output $Message
    }
}

function Test-Windows {
    $platformWindowsVariable = Get-Variable -Name IsWindows -Scope Global -ErrorAction SilentlyContinue
    if ($null -ne $platformWindowsVariable) {
        return [bool]$platformWindowsVariable.Value
    }

    return $env:OS -eq "Windows_NT"
}

function Get-ReleaseRid {
    try {
        $architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    }
    catch {
        if ([System.Environment]::Is64BitOperatingSystem) {
            return "win-x64"
        }

        throw "Unsupported Windows architecture: 32-bit Windows is not supported."
    }

    switch ($architecture) {
        "X64" {
            return "win-x64"
        }
        "Arm64" {
            throw "Windows ARM64 releases are not available yet."
        }
        default {
            throw "Unsupported Windows architecture: $architecture."
        }
    }
}

function Get-ReleaseBaseUrl {
    param([string]$RequestedVersion)

    if ([string]::IsNullOrWhiteSpace($RequestedVersion) -or
        [string]::Equals($RequestedVersion, "latest", [System.StringComparison]::OrdinalIgnoreCase)) {
        return "https://github.com/$script:Repo/releases/latest/download"
    }

    $trimmedVersion = $RequestedVersion.Trim()
    if ($trimmedVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $tag = "v$($trimmedVersion.Substring(1))"
    }
    else {
        $tag = "v$trimmedVersion"
    }

    return "https://github.com/$script:Repo/releases/download/$tag"
}

function Assert-SupportedVersion {
    param([string]$RequestedVersion)

    if ([string]::IsNullOrWhiteSpace($RequestedVersion) -or
        [string]::Equals($RequestedVersion, "latest", [System.StringComparison]::OrdinalIgnoreCase)) {
        return
    }

    $trimmedVersion = $RequestedVersion.Trim()
    if ($trimmedVersion.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $trimmedVersion = $trimmedVersion.Substring(1)
    }

    $versionForComparison = ($trimmedVersion -split '-', 2)[0]
    if ($versionForComparison -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must be latest or a semantic version such as v$script:MinimumSupportedVersion."
    }

    $parsedVersion = [version]$versionForComparison
    if ($parsedVersion -lt $script:MinimumSupportedVersion) {
        throw "Version overrides only support v$script:MinimumSupportedVersion or later because older release artifacts use a different layout."
    }
}

function Get-InstallerTemporaryDirectory {
    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("fgvm-install-" + [System.Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    return $path
}

function Invoke-Download {
    param(
        [string]$Url,
        [string]$OutputPath
    )

    Write-Info "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $OutputPath -UseBasicParsing
}

function Test-Checksum {
    param(
        [string]$ArchivePath,
        [string]$ChecksumPath
    )

    $content = Get-Content -LiteralPath $ChecksumPath -Raw
    $expected = ($content -split '\s+' | Select-Object -First 1).Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($expected)) {
        throw "Checksum file is empty."
    }

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $ArchivePath).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "Checksum verification failed. Expected $expected, got $actual."
    }

    Write-Info "Checksum verified."
}

function Split-PathList {
    param([string]$PathValue)

    if ([string]::IsNullOrWhiteSpace($PathValue)) {
        return @()
    }

    return @($PathValue -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-NormalizedPathEntry {
    param([string]$PathEntry)

    try {
        return [System.IO.Path]::GetFullPath($PathEntry.Trim()).TrimEnd('\')
    }
    catch {
        return $PathEntry.Trim().TrimEnd('\')
    }
}

function Test-PathEntry {
    param(
        [string[]]$Entries,
        [string]$PathEntry
    )

    $normalizedTarget = Get-NormalizedPathEntry $PathEntry
    foreach ($entry in $Entries) {
        if ([string]::Equals((Get-NormalizedPathEntry $entry), $normalizedTarget, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function ConvertTo-PathValue {
    param(
        [string]$PathValue,
        [string[]]$PathsToAdd,
        [string[]]$PathsToRemove
    )

    $entries = @(Split-PathList $PathValue)

    foreach ($pathToRemove in $PathsToRemove) {
        if ([string]::IsNullOrWhiteSpace($pathToRemove)) {
            continue
        }

        $normalizedRemove = Get-NormalizedPathEntry $pathToRemove
        $entries = @($entries | Where-Object {
                -not [string]::Equals((Get-NormalizedPathEntry $_), $normalizedRemove, [System.StringComparison]::OrdinalIgnoreCase)
            })
    }

    foreach ($pathToAdd in $PathsToAdd) {
        if ([string]::IsNullOrWhiteSpace($pathToAdd)) {
            continue
        }

        if (-not (Test-PathEntry -Entries $entries -PathEntry $pathToAdd)) {
            $entries += $pathToAdd
        }
    }

    return ($entries -join ';')
}

function Send-EnvironmentChangeNotification {
    try {
        if (-not ("FgvmInstaller.User32" -as [type])) {
            $signature = @'
[DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Auto)]
public static extern IntPtr SendMessageTimeout(
    IntPtr hWnd,
    uint Msg,
    UIntPtr wParam,
    string lParam,
    uint fuFlags,
    uint uTimeout,
    out UIntPtr lpdwResult
);
'@
            Add-Type -MemberDefinition $signature -Name "User32" -Namespace "FgvmInstaller" | Out-Null
        }

        $result = [UIntPtr]::Zero
        $hwndBroadcast = [IntPtr]::new(0xffff)
        $wmSettingChange = 0x1A
        $smtoAbortIfHung = 0x0002

        $null = [FgvmInstaller.User32]::SendMessageTimeout(
            $hwndBroadcast,
            $wmSettingChange,
            [UIntPtr]::Zero,
            "Environment",
            $smtoAbortIfHung,
            5000,
            [ref]$result)
    }
    catch {
        Write-Info "PATH updated, but the Windows environment change notification could not be broadcast."
    }
}

function Update-UserEnvironment {
    [CmdletBinding(SupportsShouldProcess)]
    param()

    $pathsToRemove = @()
    if ($script:LegacyFgvmupPath) {
        $pathsToRemove += $script:LegacyFgvmupPath
    }

    $currentUserPath = [System.Environment]::GetEnvironmentVariable("Path", "User")
    $pathsToAdd = @($script:InstallDir, $script:FgvmHomeBin)
    $newUserPath = ConvertTo-PathValue -PathValue $currentUserPath -PathsToAdd $pathsToAdd -PathsToRemove $pathsToRemove

    if ($PSCmdlet.ShouldProcess("user environment", "Persist fgvm installer settings")) {
        if ($script:PersistFgvmHome) {
            [System.Environment]::SetEnvironmentVariable("FGVM_HOME", $script:FgvmHome, "User")
        }

        if ($script:PersistInstallDir) {
            [System.Environment]::SetEnvironmentVariable("FGVM_INSTALL_DIR", $script:InstallDir, "User")
        }

        if ($newUserPath -ne $currentUserPath) {
            [System.Environment]::SetEnvironmentVariable("Path", $newUserPath, "User")
            Send-EnvironmentChangeNotification
            Write-Info "Updated user PATH."
        }
        else {
            Write-Info "$script:InstallDir and $script:FgvmHomeBin are already on the user PATH."
        }
    }

    $env:Path = ConvertTo-PathValue -PathValue $env:Path -PathsToAdd $pathsToAdd -PathsToRemove $pathsToRemove
    if ($script:PersistFgvmHome) {
        $env:FGVM_HOME = $script:FgvmHome
    }

    if ($script:PersistInstallDir) {
        $env:FGVM_INSTALL_DIR = $script:InstallDir
    }
}

function Install-Fgvm {
    if (-not (Test-Windows)) {
        throw "installer.ps1 currently supports Windows only. Use installer.sh on macOS or Linux."
    }

    if (-not [System.IO.Path]::IsPathRooted($script:InstallDir)) {
        throw "InstallDir must be an absolute path."
    }

    if (-not [System.IO.Path]::IsPathRooted($script:FgvmHome)) {
        throw "FgvmHome must be an absolute path."
    }

    $rid = Get-ReleaseRid
    $archiveName = "fgvm-$rid.zip"
    Assert-SupportedVersion -RequestedVersion $script:RequestedVersion
    $baseUrl = Get-ReleaseBaseUrl -RequestedVersion $script:RequestedVersion
    $archiveUrl = "$baseUrl/$archiveName"
    $checksumUrl = "$archiveUrl.sha256"
    $tempDir = Get-InstallerTemporaryDirectory

    try {
        $archivePath = Join-Path $tempDir $archiveName
        $checksumPath = Join-Path $tempDir "$archiveName.sha256"
        $extractPath = Join-Path $tempDir "extract"

        Invoke-Download -Url $archiveUrl -OutputPath $archivePath
        Invoke-Download -Url $checksumUrl -OutputPath $checksumPath
        Test-Checksum -ArchivePath $archivePath -ChecksumPath $checksumPath

        New-Item -ItemType Directory -Path $extractPath -Force | Out-Null
        Expand-Archive -LiteralPath $archivePath -DestinationPath $extractPath -Force

        $extractedBinary = Join-Path $extractPath "fgvm.exe"
        if (-not (Test-Path -LiteralPath $extractedBinary -PathType Leaf)) {
            throw "Release archive did not contain fgvm.exe."
        }

        New-Item -ItemType Directory -Path $script:InstallDir, $script:FgvmHomeBin -Force | Out-Null
        Move-Item -LiteralPath $extractedBinary -Destination $script:TargetPath -Force
        Write-Info "Installed fgvm to $script:TargetPath."

        if (-not $script:SkipPathUpdate) {
            Update-UserEnvironment
        }
        else {
            Write-Info "Skipped user environment update. Add $script:InstallDir and $script:FgvmHomeBin to PATH before running fgvm."
        }

        try {
            $installedVersion = & $script:TargetPath --version
            Write-Info "Installed $installedVersion."
        }
        catch {
            Write-Info "Installed fgvm. Run `"$script:TargetPath`" --version to verify the installation."
        }
    }
    finally {
        if (Test-Path -LiteralPath $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    Install-Fgvm
}
catch {
    Write-Error "fgvm installation failed: $_"
    exit 1
}
