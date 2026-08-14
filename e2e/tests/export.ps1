Set-StrictMode -Version Latest

function New-ExportProject {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [string] $Presets = "",
        [string] $ProjectName = "Mock Game",
        [string] $ProjectVersion = "1.2.0"
    )

    $projectPath = Join-Path $Context.WorkPath $Name
    New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

    Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value @"
[application]

config/name="$ProjectName"
config/version="$ProjectVersion"
config/features=PackedStringArray("4.6", "Forward Plus")
"@ -NoNewline

    if ($Presets) {
        Set-Content -LiteralPath (Join-Path $projectPath "export_presets.cfg") -Value $Presets -NoNewline
    }

    return $projectPath
}

function Get-ExportEnvironment {
    return @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = (Join-Path $Context.RootPath "godot-export-templates") }
}

Suite "export" {
    Test "auto-install keeps JSON stdout machine-readable" {
        $projectPath = New-ExportProject "export-json-auto-install" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game"
'@

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) `
            -Arguments @("export", "--json", "4.6")

        Assert.ExitCode 0 $export "fgvm export --json with editor auto-install"
        $manifest = $export.Stdout | ConvertFrom-Json
        Assert.Equal "Linux" $manifest.targets[0].preset
        Assert.NotContains "Installing" $export.Stdout `
            "Editor preparation status must not corrupt the JSON document."
        Assert.Contains "Installing" $export.Stderr `
            "Human-readable editor preparation status belongs on stderr in JSON mode."
    }

    Test "exports every configured preset and reports where each landed" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-happy" @'
[preset.0]
name="Linux"
platform="Linux"
runnable=true
export_path="build/linux/game"

[preset.1]
name="Web"
platform="Web"
runnable=false
export_path=""
'@

        $webOutput = Join-Path $projectPath "dist" "web"
        New-Item -ItemType Directory -Path $webOutput -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $webOutput "stale.js") -Value "stale" -NoNewline

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) -Arguments @("export", "4.6")

        Assert.ExitCode 0 $export "fgvm export"
        Assert.Contains "Linux" $export.Stdout
        Assert.Contains "Web" $export.Stdout
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "build" "linux") -PathType Container) `
            "A configured export directory should exist after Godot runs."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "build" "linux" "game") -PathType Leaf) `
            "The configured Linux executable should be produced."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "dist" "web") -PathType Container) `
            "A blank export path should fall back beneath dist."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "dist" "web" "index.html") -PathType Leaf) `
            "The fallback Web entry point should be produced."
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath "dist" "web" "stale.js") -PathType Leaf) `
            "An isolated output should atomically replace stale content."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath ".fgvm-export.json") -PathType Leaf) `
            "Every successful export should leave the default handoff manifest."
    }

    Test "does not pin the project version" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-no-pin" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game"
'@

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) -Arguments @("export", "4.6")

        Assert.ExitCode 0 $export "fgvm export with an explicit query"
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath ".fgvm-version") -PathType Leaf) `
            "Exporting must not create a version pin; only fgvm local writes one."
    }

    Test "passes the preset name to Godot as a single argument" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        $projectPath = New-ExportProject "export-arguments" @'
[preset.0]
name="Windows Demo"
platform="Windows Desktop"
export_path="build/windows/game.exe"
'@

        $invocationPath = $seeded.MockInvocationPath
        $environment = Get-ExportEnvironment

        $export = Run -Cwd $projectPath -Environment $environment -Arguments @("export", "4.6")

        Assert.ExitCode 0 $export "fgvm export with a spaced preset name"
        $invocation = (File.Read $invocationPath) | ConvertFrom-Json
        Assert.Contains "--export-release" $invocation.Arguments
        Assert.Contains "Windows Demo" $invocation.Arguments `
            "A preset name containing a space must survive as one argument."
        Assert.NotContains "Windows" $invocation.Arguments `
            "The preset name must not be split into separate arguments."
    }

    Test "writes a manifest describing each target" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-manifest" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game"
'@

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) `
            -Arguments @("export", "--manifest", "dist/manifest.json", "4.6")

        Assert.ExitCode 0 $export "fgvm export --manifest"
        $manifestPath = Join-Path $projectPath "dist" "manifest.json"
        Assert.True (Test-Path -LiteralPath $manifestPath -PathType Leaf) "The manifest should be written."

        $manifest = (File.Read $manifestPath) | ConvertFrom-Json
        Assert.Equal "1" ([string]$manifest.manifestVersion)
        Assert.Equal "4.6.2-stable-standard" $manifest.godot
        Assert.True ([guid]::Parse([string]$manifest.runId) -ne [guid]::Empty) "The manifest should identify this run."
        Assert.Equal "Linux" $manifest.targets[0].preset
        Assert.Equal "Linux" $manifest.targets[0].platform
        Assert.Equal "release" $manifest.targets[0].mode
        Assert.Equal "directory" $manifest.targets[0].kind
        Assert.Equal "build/linux" $manifest.targets[0].path
    }

    Test "emits the same payload on stdout with --json" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-json" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game"
'@

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) -Arguments @("export", "-j", "4.6")

        Assert.ExitCode 0 $export "fgvm export -j"
        $manifest = $export.Stdout | ConvertFrom-Json
        Assert.Equal "1" ([string]$manifest.manifestVersion)
        Assert.Equal "Linux" $manifest.targets[0].preset
        Assert.NotContains "Exported" $export.Stdout "JSON mode should not mix human output into stdout."
    }

    Test "an output root overrides every configured export path" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-output-root" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game.zip"

[preset.1]
name="Web"
platform="Web"
export_path="build/web/index.html"

[preset.2]
name="macOS"
platform="macOS"
export_path="build/macos/game.app"
'@

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) `
            -Arguments @("export", "--output", "raw", "--json", "4.6")

        Assert.ExitCode 0 $export "fgvm export --output raw"
        $manifest = $export.Stdout | ConvertFrom-Json
        Assert.Contains "raw/linux" $manifest.targets[0].path
        Assert.Contains "raw/web" $manifest.targets[1].path
        Assert.Contains "raw/macos/macos.app" $manifest.targets[2].path
        Assert.Equal "directory" $manifest.targets[0].kind `
            "An output root should force a raw layout even when the preset configured an archive."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "raw" "macos" "macos.app") -PathType Container) `
            "A macOS app bundle should retain its .app destination while staging."
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath "build") -PathType Container) `
            "The configured export path should be ignored entirely."
    }

    Test "archive mode asks Godot for native zip packs without writing release outputs" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-archive" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game.x86_64"

[preset.1]
name="Web"
platform="Web"
export_path="build/web/index.html"
'@

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) `
            -Arguments @("export", "--archive", "--json", "4.6")

        Assert.ExitCode 0 $export "fgvm export --archive"
        $manifest = $export.Stdout | ConvertFrom-Json
        Assert.Equal "file" $manifest.targets[0].kind
        Assert.Equal "pack" $manifest.targets[0].mode
        Assert.Contains ".zip" $manifest.targets[0].path
        Assert.Equal "file" $manifest.targets[1].kind
        Assert.Equal "pack" $manifest.targets[1].mode
        Assert.Contains ".zip" $manifest.targets[1].path

        foreach ($target in $manifest.targets) {
            $archivePath = Join-Path $projectPath ([string]$target.path)
            Assert.True (Test-Path -LiteralPath $archivePath -PathType Leaf) `
                "Every archive target should exist."
            $archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
            try {
                Assert.True ($archive.Entries.Count -gt 0) "Every archive should contain exported files."
            }
            finally {
                $archive.Dispose()
            }
        }

        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath "build" "linux" "game.x86_64") -PathType Leaf) `
            "Native pack mode should not write the configured playable release output."
    }

    Test "fails when the directory holds no Godot project" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "export-no-project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

        $export = Run -Cwd $projectPath -Arguments @("export", "4.6")

        Assert.ExitCode 78 $export "fgvm export without project.godot"
        Assert.Contains "project.godot" $export.Stderr
    }

    Test "fails when the project declares no export presets" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-no-presets" ""

        $export = Run -Cwd $projectPath -Arguments @("export", "4.6")

        Assert.ExitCode 78 $export "fgvm export without export_presets.cfg"
        Assert.Contains "export" $export.Stderr
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath ".fgvm-version") -PathType Leaf) `
            "Preflight must reject the project before any state changes."
    }

    Test "lets Godot handle configured Android and iOS presets" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-unsupported" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game"

[preset.1]
name="Android"
platform="Android"
export_path="build/android/game.apk"

[preset.2]
name="iOS"
platform="iOS"
export_path="build/ios/game.ipa"
'@

        $export = Run -Cwd $projectPath -Arguments @("export", "4.6")

        Assert.ExitCode 0 $export "fgvm export with mobile presets"
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "build" "android" "game.apk") -PathType Leaf) `
            "The Android artifact should be produced."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "build" "ios" "game.ipa") -PathType Leaf) `
            "The iOS destination should be left to Godot."
    }

    Test "fails on a malformed preset file without changing project state" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-malformed" @'
[preset.0]
name="Linux"
platform="Linux"
runnable=perhaps
'@

        $export = Run -Cwd $projectPath -Arguments @("export", "4.6")

        Assert.ExitCode 78 $export "fgvm export with a malformed preset"
        Assert.Contains "runnable" $export.Stderr
        Assert.Contains "line 4" $export.Stderr
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath ".fgvm-version") -PathType Leaf) `
            "A malformed preset file must be rejected before any state changes."
    }

    Test "fails when two presets claim the same destination" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-collision" @'
[preset.0]
name="Linux One"
platform="Linux"
export_path="build/shared/game"

[preset.1]
name="Linux Two"
platform="Linux"
export_path="build/shared/game"
'@

        $export = Run -Cwd $projectPath -Arguments @("export", "4.6")

        Assert.ExitCode 78 $export "fgvm export with colliding destinations"
        Assert.Contains "Linux One" $export.Stderr
        Assert.Contains "Linux Two" $export.Stderr
    }

    Test "a later Godot failure preserves every prior final output and invalidates the manifest" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-godot-failure" @'
[preset.0]
name="Linux"
platform="Linux"
export_path="build/linux/game"

[preset.1]
name="--fgvm-mock-fail"
platform="Linux"
export_path="build/failing/game"
'@

        $priorOutput = Join-Path $projectPath "build" "linux"
        New-Item -ItemType Directory -Path $priorOutput -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $priorOutput "game") -Value "prior" -NoNewline
        Set-Content -LiteralPath (Join-Path $projectPath ".fgvm-export.json") -Value "stale" -NoNewline

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) -Arguments @("export", "4.6")

        Assert.ExitCode 1 $export "fgvm export when Godot fails"
        Assert.Contains "failed" $export.Stderr
        Assert.Equal "prior" (File.Read (Join-Path $priorOutput "game")) `
            "The successful first target must remain staged when a later target fails."
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath "build" "failing") -PathType Container) `
            "A failed target must not create its final output."
        Assert.False (Test-Path -LiteralPath (Join-Path $projectPath ".fgvm-export.json") -PathType Leaf) `
            "A failed run must not leave a stale success manifest."
    }

    Test "leaves unrelated content in a configured export directory alone" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-shared-directory" @'
[preset.0]
name="Windows"
platform="Windows Desktop"
runnable=true
export_path="build/game.exe"
'@

        $buildPath = Join-Path $projectPath "build"
        New-Item -ItemType Directory -Path (Join-Path $buildPath "installer") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $buildPath "signing.config") -Value "secret" -NoNewline
        Set-Content -LiteralPath (Join-Path $buildPath "installer" "setup.iss") -Value "installer script" -NoNewline

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) -Arguments @("export", "4.6")

        Assert.ExitCode 0 $export "fgvm export into a shared directory"
        Assert.True (Test-Path -LiteralPath (Join-Path $buildPath "game.exe") -PathType Leaf) `
            "The export output should be produced."
        Assert.Equal "secret" (File.Read (Join-Path $buildPath "signing.config")) `
            "An unrelated file in the export directory must survive."
        Assert.Equal "installer script" (File.Read (Join-Path $buildPath "installer" "setup.iss")) `
            "An unrelated subdirectory in the export directory must survive."
    }

    Test "never replaces the project directory itself" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-ExportProject "export-project-root" @'
[preset.0]
name="Windows"
platform="Windows Desktop"
runnable=true
export_path="game.exe"
'@

        New-Item -ItemType Directory -Path (Join-Path $projectPath "src") -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "src" "player.gd") -Value "extends Node" -NoNewline

        $export = Run -Cwd $projectPath -Environment (Get-ExportEnvironment) -Arguments @("export", "4.6")

        Assert.ExitCode 0 $export "fgvm export into the project root"
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "game.exe") -PathType Leaf) `
            "The export output should be produced."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "project.godot") -PathType Leaf) `
            "project.godot must survive an export that targets the project root."
        Assert.True (Test-Path -LiteralPath (Join-Path $projectPath "export_presets.cfg") -PathType Leaf) `
            "export_presets.cfg must survive an export that targets the project root."
        Assert.Equal "extends Node" (File.Read (Join-Path $projectPath "src" "player.gd")) `
            "Project sources must survive an export that targets the project root."
    }

}
