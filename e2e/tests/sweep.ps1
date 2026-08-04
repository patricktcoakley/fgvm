Set-StrictMode -Version Latest

# A hard kill during an install or a removal leaves marker directories behind. Rather than killing a process
# mid-write, these create exactly what it would leave and then run any command, since the sweep happens at startup.
function New-Marker {
    param(
        [Parameter(Mandatory)] [string] $Parent,
        [Parameter(Mandatory)] [string] $Name,
        [TimeSpan] $Age = [TimeSpan]::Zero
    )

    $path = Join-Path $Parent $Name
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $path "contents.txt") -Value "contents"

    if ($Age -ne [TimeSpan]::Zero) {
        (Get-Item -LiteralPath $path -Force).LastWriteTimeUtc = (Get-Date).ToUniversalTime().Subtract($Age)
    }

    return $path
}

function New-Guid32 {
    return [guid]::NewGuid().ToString("N")
}

Suite "sweep" {
    Test "removes a stale install staging directory left by an interrupted install" {
        $installations = $Context.InstallationsDirectoryPath
        $staging = New-Marker $installations ".fgvm-staging-$(New-Guid32)" ([TimeSpan]::FromDays(3))

        $result = Run "--version"

        Assert.ExitCode 0 $result "fgvm --version"
        Assert.False (Test-Path -LiteralPath $staging) "A stale staging directory should be swept."
    }

    Test "removes a stale template staging directory" {
        $root = $Context.FgvmRootPath
        $staging = New-Marker $root ".fgvm-template-staging-$(New-Guid32)" ([TimeSpan]::FromDays(3))

        $result = Run "--version"

        Assert.ExitCode 0 $result "fgvm --version"
        Assert.False (Test-Path -LiteralPath $staging) "A stale template staging directory should be swept."
    }

    Test "removes a stale commit backup" {
        $installations = $Context.InstallationsDirectoryPath
        $backup = New-Marker $installations ".backup-$(New-Guid32)-4.6.2-stable" ([TimeSpan]::FromDays(3))

        $result = Run "--version"

        Assert.ExitCode 0 $result "fgvm --version"
        Assert.False (Test-Path -LiteralPath $backup) "A stale backup should be swept."
    }

    # The sweep runs on every command, so a freshly created marker may belong to an install still running in
    # another process. Deleting one of those would destroy a live install rather than reclaim a leak.
    Test "leaves a recent staging directory alone so a concurrent install is not destroyed" {
        $installations = $Context.InstallationsDirectoryPath
        $staging = New-Marker $installations ".fgvm-staging-$(New-Guid32)"

        $result = Run "--version"

        Assert.ExitCode 0 $result "fgvm --version"
        Assert.True (Test-Path -LiteralPath $staging) "A recent staging directory should survive the sweep."
    }

    Test "leaves a recent commit backup alone" {
        $installations = $Context.InstallationsDirectoryPath
        $backup = New-Marker $installations ".backup-$(New-Guid32)-4.6.2-stable"

        $result = Run "--version"

        Assert.ExitCode 0 $result "fgvm --version"
        Assert.True (Test-Path -LiteralPath $backup) "A recent backup should survive the sweep."
    }

    Test "never touches directories that only look like markers" {
        $root = $Context.FgvmRootPath
        $notes = New-Marker $root ".backup-notes" ([TimeSpan]::FromDays(30))
        $config = New-Marker $root ".config" ([TimeSpan]::FromDays(30))
        $shortGuid = New-Marker $root ".fgvm-staging-abc123" ([TimeSpan]::FromDays(30))

        $result = Run "--version"

        Assert.ExitCode 0 $result "fgvm --version"
        Assert.True (Test-Path -LiteralPath $notes) "A user's own .backup-* directory should never be swept."
        Assert.True (Test-Path -LiteralPath $config) "An unrelated dot directory should never be swept."
        Assert.True (Test-Path -LiteralPath $shortGuid) "A marker prefix without a full guid should never be swept."
    }

    Test "leaves installed versions untouched while sweeping around them" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $staging = New-Marker $Context.InstallationsDirectoryPath ".fgvm-staging-$(New-Guid32)" ([TimeSpan]::FromDays(3))

        $result = Run "list" "--json"

        Assert.ExitCode 0 $result "fgvm list"
        Assert.False (Test-Path -LiteralPath $staging) "The stale marker should be swept."
        Assert.True (Test-Path -LiteralPath $stable.InstallationPath) "The installation should be untouched."
        Assert.Equal @($stable.Name) @((Json $result.Stdout).name)
    }
}
