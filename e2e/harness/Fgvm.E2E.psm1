Set-StrictMode -Version Latest

$script:Results = [System.Collections.Generic.List[object]]::new()
$script:CurrentSuite = ""
$script:Config = $null

function Fgvm.Initialize {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $FgvmPath,

        [Parameter(Position = 1)]
        [int] $TimeoutSeconds = 60
    )

    $script:Results.Clear()
    $script:CurrentSuite = ""
    $script:Config = [pscustomobject]@{
        FgvmPath = $FgvmPath
        TimeoutSeconds = $TimeoutSeconds
    }
}

function Fgvm.AddResult {
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

function Fgvm.Results {
    foreach ($result in $script:Results) {
        $result
    }
}

function Fgvm.Suite {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    $previousSuite = $script:CurrentSuite
    $script:CurrentSuite = $Name

    Write-Host "suite - $Name"

    try {
        & $Body
    }
    finally {
        $script:CurrentSuite = $previousSuite
    }
}

function Fgvm.Test {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    $suite = if ([string]::IsNullOrWhiteSpace($script:CurrentSuite)) {
        "e2e"
    }
    else {
        $script:CurrentSuite
    }

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        Fgvm.WithHome -Name $Name -Body $Body
        $stopwatch.Stop()

        Fgvm.AddResult $suite $Name $true $stopwatch.Elapsed
        Write-Host "ok - $suite - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
    }
    catch {
        $stopwatch.Stop()

        Fgvm.AddResult $suite $Name $false $stopwatch.Elapsed $_.Exception.Message
        Write-Host "not ok - $suite - $Name ($([int] $stopwatch.Elapsed.TotalMilliseconds)ms)"
        Write-Host $_.Exception.Message
    }
}

function Fgvm.WithHome {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [scriptblock] $Body
    )

    if ($null -eq $script:Config) {
        throw "The e2e run has not been initialized."
    }

    $context = Fgvm.NewContext $Name $script:Config.FgvmPath
    $rootPath = $context.RootPath
    $bodyError = $null
    $cleanupError = $null

    try {
        & $Body -Context $context -TimeoutSeconds $script:Config.TimeoutSeconds
    }
    catch {
        $bodyError = $_
    }

    try {
        Fgvm.RemoveContext $context

        if (Test-Path -LiteralPath $rootPath) {
            throw "Temporary test root was not removed: $rootPath"
        }
    }
    catch {
        $cleanupError = $_
    }

    if ($null -ne $bodyError) {
        throw $bodyError.Exception.Message
    }

    if ($null -ne $cleanupError) {
        throw $cleanupError.Exception.Message
    }
}

function Fgvm.Platform {
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

function Fgvm.ResolvePath {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $RepoRoot
    )

    $platform = Fgvm.Platform
    $executable = if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        "fgvm.exe"
    }
    else {
        "fgvm"
    }

    Join-Path $RepoRoot ".fgvm-e2e-cli" $platform $executable
}

function Fgvm.NewContext {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Name,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $FgvmPath
    )

    $safeName = $Name -replace '[^a-zA-Z0-9._-]', '-'
    $rootPath = Join-Path ([System.IO.Path]::GetTempPath()) "fgvm-e2e-$safeName-$([System.Guid]::NewGuid().ToString('N'))"
    $homePath = Join-Path $rootPath "home"

    New-Item -ItemType Directory -Path $homePath -Force | Out-Null

    [pscustomobject]@{
        Name = $Name
        RootPath = $rootPath
        HomePath = $homePath
        FgvmPath = $FgvmPath
    }
}

function Fgvm.RemoveContext {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [pscustomobject] $Context
    )

    if (Test-Path -LiteralPath $Context.RootPath) {
        Remove-Item -LiteralPath $Context.RootPath -Recurse -Force
    }
}

function Fgvm.Process {
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

function Fgvm.Invoke {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [pscustomobject] $Context,

        [Parameter(Mandatory = $true, Position = 1)]
        [string[]] $Arguments,

        [Parameter(Position = 2)]
        [int] $TimeoutSeconds = 60
    )

    Fgvm.Process `
        -FilePath $Context.FgvmPath `
        -Arguments $Arguments `
        -Environment @{ FGVM_HOME = $Context.HomePath } `
        -TimeoutSeconds $TimeoutSeconds
}

function Fgvm.Assert {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [bool] $Condition,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Fgvm.AssertSuccess {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [pscustomobject] $Result,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $CommandName
    )

    if ($Result.ExitCode -ne 0) {
        throw @"
Command failed: $CommandName
Exit code: $($Result.ExitCode)
STDOUT:
$($Result.Stdout)
STDERR:
$($Result.Stderr)
"@
    }
}

function Fgvm.AssertContains {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Text,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Expected,

        [Parameter(Mandatory = $true, Position = 2)]
        [string] $Message
    )

    if (-not $Text.Contains($Expected, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Message Expected '$Expected'."
    }
}

function Fgvm.AssertDirectory {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Path,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $Message
    )

    Fgvm.Assert ([System.IO.Directory]::Exists($Path)) "$Message Path: $Path"
}

function Fgvm.JsonArray {
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $Json,

        [Parameter(Mandatory = $true, Position = 1)]
        [string] $CommandName
    )

    try {
        $value = $Json | ConvertFrom-Json -NoEnumerate
    }
    catch {
        throw "Failed to parse JSON from $CommandName. $($_.Exception.Message)`n$Json"
    }

    if ($value -isnot [array]) {
        throw "Expected $CommandName to return a JSON array."
    }

    Write-Output -NoEnumerate $value
}

Export-ModuleMember -Function "Fgvm.*"
