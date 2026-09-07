Set-StrictMode -Version Latest

Suite "project launch" {
    Test "forced interactive launch bypasses a legacy pin even with a query" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        $projectPath = Join-Path $Context.WorkPath "interactive-legacy-pin"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        $versionFile = Join-Path $projectPath ".fgvm-version"
        Set-Content -LiteralPath $versionFile -Value "4.5-stable" -NoNewline

        foreach ($arguments in @(@("godot", "-i"), @("godot", "-i", "--query", "4.5"))) {
            $godot = Run -Cwd $projectPath -Arguments $arguments

            # The harness redirects input, so reaching selection reports an interactive-terminal error
            Assert.ExitCode 2 $godot "fgvm godot -i with a legacy pin"
            Assert.Contains "cannot prompt" $godot.Stderr
            Assert.NotContains ".fgvm-version" $godot.Stderr
            Assert.Equal "4.5-stable" (File.Read $versionFile)
        }
    }

    Test "auto-detects project arguments and remains detached for flag-like paths" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        $projectPath = Join-Path $Context.WorkPath "my-dev-project game-server"
        $invocationPath = $seeded.MockInvocationPath
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value '[application]' -NoNewline

        $godot = Run -Cwd $projectPath -Arguments @("godot")

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
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        $projectPath = Join-Path $Context.WorkPath "explicit-argument-project"
        $invocationPath = $seeded.MockInvocationPath
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value '[application]' -NoNewline

        $godot = Run -Cwd $projectPath -Arguments @("godot", "--attached", "--args", "alpha beta")

        Assert.ExitCode 0 $godot "fgvm godot with explicit arguments"
        File.WaitFor $invocationPath
        $invocation = Json (File.Read $invocationPath)
        Assert.Equal @("alpha", "beta") @($invocation.Arguments)
        Assert.NotContains "--editor" @($invocation.Arguments)
        Assert.NotContains "--path" @($invocation.Arguments)
    }

    Test "project flag adds detected project path to explicit arguments" {
        $seeded = Add-FixtureInstallation "4.6.2-stable" -Default
        $projectPath = Join-Path $Context.WorkPath "project-argument-project"
        $invocationPath = $seeded.MockInvocationPath
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value '[application]' -NoNewline

        $godot = Run -Cwd $projectPath `
            -Arguments @("godot", "--attached", "-P", "--args", "--dump-extension-api --quit")

        Assert.ExitCode 0 $godot "fgvm godot -P with explicit arguments"
        File.WaitFor $invocationPath
        $invocation = Json (File.Read $invocationPath)
        Assert.Equal "--path" $invocation.Arguments[0]
        Assert.Equal (Split-Path -Leaf $projectPath) (Split-Path -Leaf $invocation.Arguments[1])
        Assert.Equal "--dump-extension-api" $invocation.Arguments[2]
        Assert.Equal "--quit" $invocation.Arguments[3]
    }

    Test "project flag reports when no project file is detected" {
        Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null
        $emptyDirectory = Join-Path $Context.WorkPath "empty-project-directory"
        New-Item -ItemType Directory -Path $emptyDirectory -Force | Out-Null

        $godot = Run -Cwd $emptyDirectory -Arguments @("godot", "-P")

        Assert.ExitCode 1 $godot "fgvm godot -P without a project.godot file"
        Assert.Contains "No project.godot file was detected in the current directory." $godot.Stderr
        Assert.NotContains "Something went wrong" $godot.Stderr
    }
}
