Set-StrictMode -Version Latest

Suite "godot launch" {
    Test "launches the seeded default and records its directory" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default

        $godot = Run "godot" "--attached" "--args" "--fgvm-mock-print-directory"

        Assert.ExitCode 0 $godot "fgvm godot --fgvm-mock-print-directory"
        Assert.Contains $seeded.RelativePath ($godot.Stdout.Replace("`r`n", "").Replace("`n", "").Split(@("`t", " "), [System.StringSplitOptions]::RemoveEmptyEntries) -join ' ').Trim()

        $entry = (Manifest.From $Context.InstallationsPath)["installations"][$seeded.Key]
        Assert.NotEqual $null $entry["lastLaunchedAt"]
    }

    Test "prefers a local version over the global default" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $older = Add-FixtureInstallation "4.5-stable"
        $projectPath = Join-Path $Context.WorkPath "local-launch"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath ".fgvm-version") -Value $older.Name -NoNewline

        $godot = Run -Cwd $projectPath "godot" "--attached" "--args" "--fgvm-mock-print-directory"

        Assert.ExitCode 0 $godot "fgvm godot with local version"
        $compactOutput = ($godot.Stdout.Replace("`r`n", "").Replace("`n", "").Split(@("`t", " "), [System.StringSplitOptions]::RemoveEmptyEntries) -join ' ').Trim()
        Assert.Contains $older.RelativePath $compactOutput
        Assert.NotContains $stable.RelativePath $compactOutput
    }

    Test "launches a queried installed version instead of the default" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $older = Add-FixtureInstallation "4.5-stable"
        $invocationPath = Join-Path $Context.WorkPath "queried-launch.json"

        $godot = Run -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } `
            -Arguments @("godot", "--attached", "--query", "4.5-stable-standard", "--args", "alpha beta")

        Assert.ExitCode 0 $godot "fgvm godot --query 4.5-stable-standard --args `"alpha beta`""
        Assert.Contains "Mock Godot launched with: alpha beta" $godot.Stdout

        File.WaitFor $invocationPath
        $invocation = Read-MockInvocation $invocationPath
        Assert.Contains $older.RelativePath $invocation.WorkingDirectory
        Assert.NotContains $stable.RelativePath $invocation.WorkingDirectory
        Assert.Equal @("alpha", "beta") @($invocation.Arguments)
    }

    Test "forwards arguments to mock godot" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null

        $godot = Run "godot" "--attached" "--args" "alpha beta"

        Assert.ExitCode 0 $godot "fgvm godot argument forwarding"
        Assert.Contains "Mock Godot launched with: alpha beta" $godot.Stdout
    }

    Test "propagates mock godot exit codes in attached mode" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null

        $invalid = Run "godot" "--attached" "--args" "--fgvm-mock-invalid-arg"
        $failure = Run "godot" "--attached" "--args" "--fgvm-mock-fail"

        Assert.ExitCode 2 $invalid "mock Godot argument failure"
        Assert.Contains "invalid argument" $invalid.Stdout
        Assert.ExitCode 42 $failure "mock Godot process failure"
        Assert.Contains "failure" $failure.Stdout
    }

    Test "returns before a detached godot process exits" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null
        $invocationPath = Join-Path $Context.WorkPath "detached-lifecycle.json"

        $godot = Run -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } `
            -Arguments @("godot", "--args", "--fgvm-mock-delay-ms 5000")

        Assert.ExitCode 0 $godot "fgvm detached Godot launch"
        File.WaitFor $invocationPath
        $invocation = Read-MockInvocation $invocationPath
        $process = $null

        try {
            try {
                $process = [System.Diagnostics.Process]::GetProcessById($invocation.ProcessId)
            }
            catch [System.ArgumentException] {
                throw "Detached launch should return while the mock Godot process is still running."
            }

            Assert.False $process.HasExited "Detached Godot process should still be running after fgvm exits."
        }
        finally {
            if ($null -ne $process -and -not $process.HasExited) {
                $process.Kill($true)
                [void] $process.WaitForExit(5000)
            }

            if ($null -ne $process) {
                $process.Dispose()
            }
        }
    }

    Test "does not record a launch when godot fails to start" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        Remove-Item -LiteralPath $seeded.ExecutablePath -Force

        $godot = Run "godot" "--args" "--windowed"

        Assert.ExitCode 1 $godot "fgvm launch with a missing Godot executable"
        $normalizedOutput = ($godot.Stdout -replace "\s+", " ").Trim()
        Assert.Contains "Something went wrong when trying to launch Godot" $normalizedOutput
        $entry = (Manifest.From $Context.InstallationsPath)["installations"][$seeded.Key]
        Assert.Equal $null $entry["lastLaunchedAt"]
    }
}
