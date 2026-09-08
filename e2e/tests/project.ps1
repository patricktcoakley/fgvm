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

    Test "installs matching standard export templates when both runtimes are installed" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "export-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "export_presets.cfg") -Value @'
[preset.0]
name="Web"
platform="Web"
runnable=false
export_path="build/web/index.html"
'@ -NoNewline

        $templatesRoot = Join-Path $Context.RootPath "godot-[export-templates"
        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $templatesRoot }
        $templatePayload = Join-Path $templatesRoot "4.6.2.stable" "mock-template.txt"

        $local = Run -Cwd $projectPath -Environment $environment -Arguments @("local", "4.6", "standard")

        Assert.ExitCode 0 $local "fgvm local 4.6 standard with export presets"
        Assert.Contains "Finished installing export templates" $local.Stdout
        Assert.Equal "4.6.2-stable-standard" (File.Read (Join-Path $projectPath ".fgvm-version")).Trim()
        Assert.True (Test-Path -LiteralPath $templatePayload -PathType Leaf) "Local should install the full matching template package."
        Assert.Equal "mock export template for 4.6.2-stable standard" (File.Read $templatePayload)
    }

    Test "retains the local version when export template installation fails" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "failing-template-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "export_presets.cfg") -Value @'
[preset.0]
name="Web"
platform="Web"
runnable=false
export_path="build/web/index.html"
'@ -NoNewline

        $versionPath = Join-Path $projectPath ".fgvm-version"
        $environment = @{
            FGVM_GODOT_EXPORT_TEMPLATES_DIR    = Join-Path $Context.RootPath "failed-export-templates"
            FGVM_INTEGRATION_FIXTURE_MANIFEST = Join-Path $Context.WorkPath "missing-fixture-manifest.json"
        }

        $local = Run -Cwd $projectPath -Environment $environment -Arguments @("local", "4.6")
        $output = ($local.Stdout -replace "\s+", " ").Trim()

        Assert.ExitCode 1 $local "fgvm local 4.6 with unavailable export templates"
        Assert.Contains "Set local version to 4.6.2-stable-standard" $output
        Assert.Contains "but export template installation" $output
        Assert.Contains "Release catalog hydration failed" $output
        Assert.NotContains "Finished installing export templates" $output
        Assert.Equal "4.6.2-stable-standard" (File.Read $versionPath).Trim()
    }

    Test "selects a mono local version and matching templates" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "mono-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "export_presets.cfg") -Value @'
[preset.0]
name="Linux"
platform="Linux/X11"
runnable=true
export_path="build/linux/game.x86_64"
'@ -NoNewline
        $templatesRoot = Join-Path $Context.RootPath "godot-export-templates"
        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $templatesRoot }
        $templatePayload = Join-Path $templatesRoot "4.6.2.stable.mono" "mock-template.txt"

        $local = Run -Cwd $projectPath -Environment $environment -Arguments @("local", "4.6", "mono")

        Assert.ExitCode 0 $local "fgvm local 4.6 mono"
        Assert.Equal "4.6.2-stable-mono" (File.Read (Join-Path $projectPath ".fgvm-version")).Trim()
        Assert.Equal "mock export template for 4.6.2-stable mono" (File.Read $templatePayload)
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
        Assert.Contains "Version 9.999 could not be found" $unavailable.Stderr
        Assert.NotContains "[red]" $unavailable.Stderr

        Set-Content -LiteralPath (Join-Path $projectPath ".fgvm-version") -Value "not-a-version" -NoNewline
        $godot = Run -Cwd $projectPath "godot" "--args" "--version"

        Assert.ExitCode 1 $godot "fgvm godot with malformed .fgvm-version"
        Assert.Contains '.fgvm-version' ($godot.Stderr.Replace("`r`n", "").Replace("`n", "").Split(@("`t", " "), [System.StringSplitOptions]::RemoveEmptyEntries) -join ' ').Trim()
        Assert.Equal "" $godot.Stdout.Trim() "Version-resolution failures must not leak onto stdout."
    }
}
