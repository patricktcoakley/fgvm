Set-StrictMode -Version Latest

Suite "startup" {
    Test "prints a version" {
        Assert.True (Test-Path -LiteralPath $Context.FgvmPath -PathType Leaf) "fgvm executable should exist."

        $version = Run "--version"

        Assert.ExitCode 0 $version "fgvm --version"
        Assert.NotEmpty $version.Stdout "fgvm --version should write a version."
    }

    Test "prints help with no arguments" {
        $help = Run

        Assert.ExitCode 0 $help "fgvm"
        Assert.Contains "Usage:" $help.Stdout "fgvm should show usage."
        Assert.Contains "Commands:" $help.Stdout "fgvm should show commands."
    }

    Test "prints help with help flag" {
        $help = Run "--help"

        Assert.ExitCode 0 $help "fgvm --help"
        Assert.Contains "Usage:" $help.Stdout "fgvm --help should show usage."
        Assert.Contains "Commands:" $help.Stdout "fgvm --help should show commands."
    }

    Test "prints help for an unknown command" {
        $help = Run "unknown-command"

        Assert.ExitCode 0 $help "fgvm unknown-command"
        Assert.Contains "Usage:" $help.Stdout
        Assert.Contains "Commands:" $help.Stdout
    }

    Test "prints help for every command" {
        $help = Run "--help"
        $commands = @($help.Stdout.Split("`n", [System.StringSplitOptions]::None) | ForEach-Object {
                if ($_ -match '^\s{2}(\w+)') { $Matches[1] }
            })

        foreach ($command in $commands) {
            $help = Run $command "--help"

            Assert.ExitCode 0 $help "fgvm $command --help"
            Assert.Contains "Usage:" $help.Stdout "fgvm $command --help should show usage."
        }
    }

    Test "rejects invalid command options" {
        $invalid = Run "list" "--installed"

        Assert.ExitCode 2 $invalid "fgvm list --installed"
        Assert.Contains "--installed" $invalid.Stdout
    }

    Test "lists no installed versions in a fresh home" {
        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list --json"
        Assert.Empty (Json $list.Stdout) "A fresh FGVM_HOME should not have installed versions."
    }

    Test "prints empty installation state as text" {
        $list = Run "list"
        $which = Run "which"

        Assert.ExitCode 0 $list "fgvm list"
        Assert.Contains "No installations found" $list.Stdout
        Assert.ExitCode 0 $which "fgvm which"
        Assert.Contains "No Godot version" $which.Stdout
    }

    Test "creates fgvm directories in FGVM_HOME" {
        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list --json"

        Assert.True (Test-Path -LiteralPath $Context.FgvmRootPath -PathType Container) "fgvm should create its root directory in FGVM_HOME."
        Assert.True (Test-Path -LiteralPath $Context.BinPath -PathType Container) "fgvm should create its bin directory in FGVM_HOME."
    }

    Test "which reports no selected version in a fresh home" {
        $which = Run "which" "--json"

        Assert.ExitCode 0 $which "fgvm which --json"
        $view = Json $which.Stdout

        Assert.False $view.hasVersion "A fresh FGVM_HOME should not have a selected Godot version."
        Assert.Equal $null $view.executablePath
        Assert.Contains "No Godot version" $view.message
    }

    Test "uses an overridden FGVM_HOME without creating the default root" {
        $customHome = Join-Path $Context.RootPath "custom-home"
        $list = Run -Environment @{ FGVM_HOME = $customHome } -Arguments @("list", "--json")

        Assert.ExitCode 0 $list "fgvm list --json with custom FGVM_HOME"
        Assert.True (Test-Path -LiteralPath $customHome -PathType Container) "The custom FGVM_HOME should be the fgvm root."
        Assert.False (Test-Path -LiteralPath $Context.BinPath) "The default test FGVM_HOME should remain unused."
    }

    Test "godot fails cleanly when no version is selected" {
        $godot = Run "godot" "--args" "--version"

        Assert.ExitCode 1 $godot "fgvm godot without a selected version"
        Assert.Contains "No current Godot version set" $godot.Stdout
    }
}
