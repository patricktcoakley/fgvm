Set-StrictMode -Version Latest

function Read-SelectedArtifactTarget {
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
        $lines = Get-Content -LiteralPath $Context.SelectedArtifactPath
        $urlLine = $lines | Where-Object { $_.StartsWith('URL=') } | Select-Object -First 1

        if (-not $urlLine) {
            throw "Windows selected-version shortcut does not contain a URL."
        }

        return ([uri] $urlLine.Substring(4)).LocalPath
    }

    (Get-Item -LiteralPath $Context.SelectedArtifactPath -Force).LinkTarget
}

Suite "launch artifacts" {
    Test "set creates a working PATH shim and selected-version artifact" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"

        $set = Run "set" "4.6"

        Assert.ExitCode 0 $set "fgvm set 4.6"
        Assert.True (Test-Path -LiteralPath $Context.ShimPath -PathType Leaf) "set should create the stable Godot shim."
        Assert.True (Test-Path -LiteralPath $Context.SelectedArtifactPath) "set should create the selected-version artifact."
        Assert.Equal ([System.IO.Path]::GetFullPath($seeded.ShortcutTargetPath)) ([System.IO.Path]::GetFullPath((Read-SelectedArtifactTarget)))

        if (-not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) {
            $mode = [System.IO.File]::GetUnixFileMode($Context.ShimPath)
            Assert.True $mode.HasFlag([System.IO.UnixFileMode]::UserExecute) "The generated shim should be executable."
        }

        $invocationPath = Join-Path $Context.WorkPath "shim-invocation.json"
        $shim = Invoke-GodotShim -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } -Arguments @("shim-probe")

        Assert.ExitCode 0 $shim "Godot PATH shim"
        File.WaitFor $invocationPath
        $invocation = Read-MockInvocation $invocationPath
        Process.WaitForExit $invocation.ProcessId
        Assert.Contains $seeded.RelativePath $invocation.BaseDirectory
        Assert.Equal @("shim-probe") @($invocation.Arguments)
    }

    Test "switching versions updates the artifact and shim selection" {
        $stable = Add-FixtureInstallation "4.6.2-stable"
        $older = Add-FixtureInstallation "4.5-stable"

        Assert.ExitCode 0 (Run "set" "4.6") "fgvm set 4.6"
        Assert.Equal ([System.IO.Path]::GetFullPath($stable.ShortcutTargetPath)) ([System.IO.Path]::GetFullPath((Read-SelectedArtifactTarget)))

        Assert.ExitCode 0 (Run "set" "4.5") "fgvm set 4.5"
        Assert.Equal ([System.IO.Path]::GetFullPath($older.ShortcutTargetPath)) ([System.IO.Path]::GetFullPath((Read-SelectedArtifactTarget)))

        $invocationPath = Join-Path $Context.WorkPath "switched-shim-invocation.json"
        $shim = Invoke-GodotShim -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } -Arguments @("switch-probe")

        Assert.ExitCode 0 $shim "Godot PATH shim after switching versions"
        File.WaitFor $invocationPath
        $invocation = Read-MockInvocation $invocationPath
        Process.WaitForExit $invocation.ProcessId
        Assert.Contains $older.RelativePath $invocation.BaseDirectory
        Assert.NotContains $stable.RelativePath $invocation.BaseDirectory
    }

    Test "removing the default clears the selected artifact but keeps the shim" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Assert.ExitCode 0 (Run "set" "4.6") "fgvm set 4.6"

        $remove = Run "remove" "4.6"

        Assert.ExitCode 0 $remove "fgvm remove 4.6"
        Assert.False (Test-Path -LiteralPath $Context.SelectedArtifactPath) "Removing the default should clear its selected-version artifact."
        Assert.True (Test-Path -LiteralPath $Context.ShimPath -PathType Leaf) "The stable PATH shim should remain installed."
    }
}
