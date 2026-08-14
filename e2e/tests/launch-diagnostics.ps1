Set-StrictMode -Version Latest

function Remove-SeededExecutable {
    param([Parameter(Mandatory)] $Seeded)

    Remove-Item -LiteralPath $Seeded.ExecutablePath -Force
}

function Corrupt-SeededExecutable {
    param([Parameter(Mandatory)] $Seeded)

    Set-Content -LiteralPath $Seeded.ExecutablePath -Value "this is not a Godot binary" -NoNewline
}

function New-GodotProject {
    param([Parameter(Mandatory)] [string] $Name)

    $projectPath = Join-Path $Context.WorkPath $Name
    New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value @"
[application]

config/name="Diagnostics"
config/features=PackedStringArray("4.6", "Forward Plus")
"@ -NoNewline

    return $projectPath
}

Suite "launch diagnostics" {
    Test "attached: a missing executable names the path on stderr" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        Remove-SeededExecutable $seeded

        $godot = Run "godot" "--attached"

        Assert.NotEqual 0 $godot.ExitCode "A missing executable must fail."
        Assert.Contains $seeded.Name $godot.Stderr `
            "The failure must name the installation that could not be started."
        Assert.Equal "" $godot.Stdout.Trim() `
            "Diagnostics belong on stderr so stdout stays machine-readable."
    }

    Test "attached: an unrunnable executable is diagnosed differently from a missing one" {
        $missing = Add-FixtureInstallation "4.6.2-stable" -Default
        Remove-SeededExecutable $missing
        $missingRun = Run "godot" "--attached"

        $corrupt = Add-FixtureInstallation "4.5-stable"
        Corrupt-SeededExecutable $corrupt
        $corruptRun = Run "godot" "--attached" "--query" $corrupt.Name

        Assert.NotEqual 0 $corruptRun.ExitCode "An unrunnable executable must fail."
        Assert.NotEqual "" $corruptRun.Stderr.Trim() "The failure must be reported on stderr."
        Assert.NotEqual $missingRun.Stderr.Trim() $corruptRun.Stderr.Trim() `
            "A missing editor and an unrunnable one must not produce identical diagnostics."
    }

    Test "attached: a non-zero exit reports the code and Godot's own message" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null

        $godot = Run "godot" "--attached" "--args" "--fgvm-mock-fail"

        Assert.NotEqual 0 $godot.ExitCode "A non-zero Godot exit must fail the command."
        Assert.Contains "Mock Godot failure." $godot.Stderr `
            "Godot's own stderr must reach the user's stderr."
    }

    Test "detached: a damaged editor does not report a process id" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        Corrupt-SeededExecutable $seeded

        $godot = Run "godot"

        Assert.NotEqual 0 $godot.ExitCode "A detached launch of a damaged editor must fail."
        Assert.Contains $seeded.Name $godot.Stderr `
            "The detached failure must name the editor that died during startup."
        Assert.Equal "" $godot.Stdout.Trim() `
            "A failed detached launch must not emit a success message on stdout."
        Assert.NotContains "PID" $godot.Stdout `
            "Reporting a process id for an editor that never ran states the opposite of the truth."
    }

    Test "detached: a healthy editor still launches" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        $invocationPath = $seeded.MockInvocationPath

        $godot = Run "godot" "--args" "--fgvm-mock-delay-ms 1000"
        File.WaitFor $invocationPath
        $invocation = Read-MockInvocation $invocationPath

        try {
            Assert.ExitCode 0 $godot "A healthy detached launch must still succeed."
            Assert.Contains "PID" $godot.Stdout "A successful detached launch still reports its process id."
        }
        finally {
            # Windows keeps a running executable locked, so let the mock exit before context cleanup.
            Process.WaitForExit $invocation.ProcessId
        }
    }

    Test "finite successful commands run attached instead of becoming launch failures" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null

        $godot = Run "godot" "--args" "--quit"

        Assert.ExitCode 0 $godot "A successful finite Godot command must not be mistaken for a failed editor launch."
        Assert.Contains "attached mode" $godot.Stdout
        Assert.Equal "" $godot.Stderr.Trim()
    }

    Test "export: failures are reported on stderr" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-GodotProject "export-stderr"
        Set-Content -LiteralPath (Join-Path $projectPath "export_presets.cfg") -Value @'
[preset.0]
name="--fgvm-mock-fail"
platform="Linux"
export_path="build/linux/game"
'@ -NoNewline

        $export = Run -Cwd $projectPath -Environment @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = (Join-Path $Context.RootPath "templates") } `
            -Arguments @("export", "4.6")

        Assert.NotEqual 0 $export.ExitCode "A failed export must exit non-zero."
        Assert.Contains "failed" $export.Stderr "An export failure must be reported on stderr."
        Assert.NotContains "failed" $export.Stdout "An export failure must not be reported on stdout."
    }

    Test "export: reports the actionable line, not only the tail" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = New-GodotProject "export-noisy"
        Set-Content -LiteralPath (Join-Path $projectPath "export_presets.cfg") -Value @'
[preset.0]
name="--fgvm-mock-noisy-fail"
platform="Linux"
export_path="build/linux/game"
'@ -NoNewline

        $export = Run -Cwd $projectPath -Environment @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = (Join-Path $Context.RootPath "templates") } `
            -Arguments @("export", "4.6")

        Assert.NotEqual 0 $export.ExitCode "A failed export must exit non-zero."
        Assert.Contains "the export template is missing" $export.Stderr `
            "Godot reports the cause before it gives up, so a fixed tail of its stderr hides it."
        Assert.Contains "Mock Godot follow-up 40." $export.Stderr `
            "The bounded diagnostic summary must retain the tail."
        Assert.Contains "21 more line(s)" $export.Stderr `
            "The diagnostic summary must report how many middle lines it omitted."
        Assert.NotContains "Mock Godot follow-up 20." $export.Stderr `
            "The diagnostic summary must not retain its omitted middle."
    }
}
