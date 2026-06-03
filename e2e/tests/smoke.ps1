Set-StrictMode -Version Latest

Fgvm.Suite "fgvm smoke" {
    Fgvm.Test "prints a version" {
        param(
            [pscustomobject] $Context,
            [int] $TimeoutSeconds
        )

        Fgvm.Assert ([System.IO.File]::Exists($Context.FgvmPath)) "fgvm executable should exist."

        $version = Fgvm.Invoke $Context @("--version") $TimeoutSeconds

        Fgvm.AssertSuccess $version "fgvm --version"
        Fgvm.Assert (-not [string]::IsNullOrWhiteSpace($version.Stdout)) "fgvm --version should write a version."
    }

    Fgvm.Test "prints help" {
        param(
            [pscustomobject] $Context,
            [int] $TimeoutSeconds
        )

        $help = Fgvm.Invoke $Context @("--help") $TimeoutSeconds

        Fgvm.AssertSuccess $help "fgvm --help"
        Fgvm.AssertContains $help.Stdout "Usage:" "fgvm --help should show usage."
        Fgvm.AssertContains $help.Stdout "Commands:" "fgvm --help should show commands."
    }

    Fgvm.Test "lists no installed versions in a fresh home" {
        param(
            [pscustomobject] $Context,
            [int] $TimeoutSeconds
        )

        $list = Fgvm.Invoke $Context @("list", "--json") $TimeoutSeconds

        Fgvm.AssertSuccess $list "fgvm list --json"
        $items = Fgvm.JsonArray $list.Stdout "fgvm list --json"
        Fgvm.Assert (@($items).Count -eq 0) "A fresh FGVM_HOME should not have installed versions."
    }

    Fgvm.Test "creates fgvm directories in FGVM_HOME" {
        param(
            [pscustomobject] $Context,
            [int] $TimeoutSeconds
        )

        $list = Fgvm.Invoke $Context @("list", "--json") $TimeoutSeconds

        Fgvm.AssertSuccess $list "fgvm list --json"
        $fgvmRoot = Join-Path $Context.HomePath "fgvm"
        Fgvm.AssertDirectory $fgvmRoot "fgvm should create its root directory in FGVM_HOME."
        Fgvm.AssertDirectory (Join-Path $fgvmRoot "bin") "fgvm should create its bin directory in FGVM_HOME."
    }
}
