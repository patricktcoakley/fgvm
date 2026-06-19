Set-StrictMode -Version Latest

Suite "project launch" {
    Test "auto-detects project arguments and remains detached for flag-like paths" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        $projectPath = Join-Path $Context.WorkPath "my-dev-project game-server"
        $invocationPath = Join-Path $Context.WorkPath "project-launch.json"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value '[application]' -NoNewline

        $godot = Run -Cwd $projectPath -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } -Arguments @("godot")

        Assert.ExitCode 0 $godot "fgvm godot with auto-detected project"
        Assert.Contains "Auto-detected project file" $godot.Stdout
        Assert.Contains "detached mode" $godot.Stdout
        Assert.NotContains "attached mode due to arguments" $godot.Stdout

        File.WaitFor $invocationPath
        $invocation = Read-MockInvocation $invocationPath
        Process.WaitForExit $invocation.ProcessId
        Assert.Equal "--editor" $invocation.Arguments[0]
        Assert.Equal "--path" $invocation.Arguments[1]
        Assert.Equal (Split-Path -Leaf $projectPath) (Split-Path -Leaf $invocation.Arguments[2])
        Assert.True (Test-Path -LiteralPath (Join-Path $invocation.Arguments[2] "project.godot") -PathType Leaf) "The project path passed to Godot should resolve to the detected project."
        Assert.Contains $seeded.RelativePath $invocation.WorkingDirectory

        $entry = (Manifest.From $Context.InstallationsPath)["installations"][$seeded.Key]
        Assert.NotEqual $null $entry["lastLaunchedAt"]
    }

    Test "explicit arguments suppress automatic project arguments" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null
        $projectPath = Join-Path $Context.WorkPath "explicit-argument-project"
        $invocationPath = Join-Path $Context.WorkPath "explicit-project-launch.json"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value '[application]' -NoNewline

        $godot = Run -Cwd $projectPath -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } `
            -Arguments @("godot", "--attached", "--args", "alpha beta")

        Assert.ExitCode 0 $godot "fgvm godot with explicit arguments"
        File.WaitFor $invocationPath
        $invocation = Json (File.Read $invocationPath)
        Assert.Equal @("alpha", "beta") @($invocation.Arguments)
        Assert.NotContains "--editor" @($invocation.Arguments)
        Assert.NotContains "--path" @($invocation.Arguments)
    }

    Test "project flag adds detected project path to explicit arguments" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null
        $projectPath = Join-Path $Context.WorkPath "project-argument-project"
        $invocationPath = Join-Path $Context.WorkPath "project-argument-launch.json"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value '[application]' -NoNewline

        $godot = Run -Cwd $projectPath -Environment @{ FGVM_MOCK_INVOCATION_PATH = $invocationPath } `
            -Arguments @("godot", "--attached", "-P", "--args", "--dump-extension-api --quit")

        Assert.ExitCode 0 $godot "fgvm godot -P with explicit arguments"
        File.WaitFor $invocationPath
        $invocation = Json (File.Read $invocationPath)
        Assert.Equal "--path" $invocation.Arguments[0]
        Assert.Equal (Split-Path -Leaf $projectPath) (Split-Path -Leaf $invocation.Arguments[1])
        Assert.Equal "--dump-extension-api" $invocation.Arguments[2]
        Assert.Equal "--quit" $invocation.Arguments[3]
    }
}
