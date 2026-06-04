Set-StrictMode -Version Latest

Suite "startup" {
    Test "prints a version" {
        Assert.True (Test-Path -LiteralPath $Context.FgvmPath -PathType Leaf) "fgvm executable should exist."

        $version = Run "--version"

        Assert.ExitCode 0 $version "fgvm --version"
        Assert.NotEmpty $version.Stdout "fgvm --version should write a version."
    }

    Test "prints help" {
        $help = Run "--help"

        Assert.ExitCode 0 $help "fgvm --help"
        Assert.Contains "Usage:" $help.Stdout "fgvm --help should show usage."
        Assert.Contains "Commands:" $help.Stdout "fgvm --help should show commands."
    }

    Test "lists no installed versions in a fresh home" {
        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list --json"
        Assert.Empty (Json $list.Stdout) "A fresh FGVM_HOME should not have installed versions."
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
}
