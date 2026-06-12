Set-StrictMode -Version Latest

Suite "project versions" {
    Test "creates and updates a local version file" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Add-FixtureInstallation "4.5-stable" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

        $create = Run -Cwd $projectPath "local" "4.6"
        $versionPath = Join-Path $projectPath ".fgvm-version"

        Assert.ExitCode 0 $create "fgvm local 4.6"
        Assert.Contains "Created" $create.Stdout
        Assert.Equal "4.6.2-stable-standard" (File.Read $versionPath).Trim()

        $update = Run -Cwd $projectPath "local" "4.5"

        Assert.ExitCode 0 $update "fgvm local 4.5"
        Assert.Contains "Updated" $update.Stdout
        Assert.Equal "4.5-stable-standard" (File.Read $versionPath).Trim()
    }

    Test "selects a mono local version" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "mono-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

        $local = Run -Cwd $projectPath "local" "4.6" "mono"

        Assert.ExitCode 0 $local "fgvm local 4.6 mono"
        Assert.Equal "4.6.2-stable-mono" (File.Read (Join-Path $projectPath ".fgvm-version")).Trim()
    }

    Test "detects an installed version from project godot" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "detected-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value @'
[application]
config/name="Test Project"
config/features=PackedStringArray("4.6", "Forward Plus")
'@ -NoNewline

        $local = Run -Cwd $projectPath "local"

        Assert.ExitCode 0 $local "fgvm local with project.godot"
        Assert.Equal "4.6.2-stable-standard" (File.Read (Join-Path $projectPath ".fgvm-version")).Trim()
    }

    Test "rejects malformed and unavailable local versions" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "invalid-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

        $unavailable = Run -Cwd $projectPath "local" "9.999"

        Assert.ExitCode 2 $unavailable "fgvm local 9.999"

        Set-Content -LiteralPath (Join-Path $projectPath ".fgvm-version") -Value "not-a-version" -NoNewline
        $godot = Run -Cwd $projectPath "godot" "--args" "--version"

        Assert.ExitCode 1 $godot "fgvm godot with malformed .fgvm-version"
        Assert.Contains '.fgvm-version' ($godot.Stdout -replace '\r?\n', '' -replace '[ \t]+', ' ').Trim()
    }
}
