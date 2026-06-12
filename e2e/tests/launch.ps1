Set-StrictMode -Version Latest

Suite "godot launch" {
    Test "launches the seeded default and records its directory" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default

        $godot = Run "godot" "--attached" "--args" "--fgvm-mock-print-directory"

        Assert.ExitCode 0 $godot "fgvm godot --fgvm-mock-print-directory"
        Assert.Contains $seeded.RelativePath ($godot.Stdout -replace '\r?\n', '' -replace '[ \t]+', ' ').Trim()

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
        $compactOutput = ($godot.Stdout -replace '\r?\n', '' -replace '[ \t]+', ' ').Trim()
        Assert.Contains $older.RelativePath $compactOutput
        Assert.NotContains $stable.RelativePath $compactOutput
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
}
