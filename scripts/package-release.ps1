<#
.SYNOPSIS
Packages an fgvm release executable and creates its SHA-256 checksum.

.DESCRIPTION
Creates a ZIP archive for Windows runtime identifiers and a permission-preserving
tar.gz archive for macOS and Linux runtime identifiers. Unix executables are set
to mode 0755 before archiving.

.PARAMETER Rid
The .NET runtime identifier used in the artifact name, such as win-x64,
osx-arm64, or linux-arm64.

.PARAMETER Source
The path to the published fgvm executable.

.PARAMETER OutputDirectory
The directory where the release archive and matching .sha256 file are written.
Defaults to the current directory.

.OUTPUTS
System.Management.Automation.PSCustomObject

Returns the artifact name, artifact path, and checksum path.

.EXAMPLE
./scripts/package-release.ps1 `
    -Rid osx-arm64 `
    -Source ./Fgvm.Cli/bin/Release/net10.0/osx-arm64/publish/fgvm

Creates fgvm-osx-arm64.tar.gz and fgvm-osx-arm64.tar.gz.sha256 in the current
directory.

.EXAMPLE
./scripts/package-release.ps1 `
    -Rid win-x64 `
    -Source ./Fgvm.Cli/bin/Release/net10.0/win-x64/publish/fgvm.exe `
    -OutputDirectory ./artifacts

Creates the Windows ZIP archive and checksum in ./artifacts.

.NOTES
The tar and chmod commands must be available when packaging macOS or Linux
artifacts.
#>
#Requires -Version 7.0

[CmdletBinding()]
[OutputType([pscustomobject])]
param(
    [Parameter(Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string]$Rid,

    [Parameter(Mandatory)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$Source,

    [string]$OutputDirectory = "."
)

$ErrorActionPreference = "Stop"

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$sourceDirectory = Split-Path -Parent $sourcePath
$sourceName = Split-Path -Leaf $sourcePath
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$isWindowsArtifact = $Rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)
$extension = $isWindowsArtifact ? ".zip" : ".tar.gz"
$artifactName = "fgvm-$Rid$extension"
$artifactPath = Join-Path $outputPath $artifactName
$checksumPath = "$artifactPath.sha256"

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Remove-Item -LiteralPath $artifactPath, $checksumPath -Force -ErrorAction SilentlyContinue

if ($isWindowsArtifact) {
    Compress-Archive -LiteralPath $sourcePath -DestinationPath $artifactPath -Force
}
else {
    & chmod 755 $sourcePath
    if ($LASTEXITCODE -ne 0) {
        throw "chmod failed with exit code $LASTEXITCODE."
    }

    $originalCopyfileDisable = $env:COPYFILE_DISABLE
    try {
        $env:COPYFILE_DISABLE = "1"
        & tar -czf $artifactPath -C $sourceDirectory $sourceName
        if ($LASTEXITCODE -ne 0) {
            throw "tar failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        if ($null -eq $originalCopyfileDisable) {
            Remove-Item Env:COPYFILE_DISABLE -ErrorAction SilentlyContinue
        }
        else {
            $env:COPYFILE_DISABLE = $originalCopyfileDisable
        }
    }
}

$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $artifactPath).Hash.ToLowerInvariant()
"$hash  $artifactName" | Set-Content -LiteralPath $checksumPath -Encoding utf8NoBOM

[pscustomobject]@{
    ArtifactName = $artifactName
    ArtifactPath = $artifactPath
    ChecksumPath = $checksumPath
}
