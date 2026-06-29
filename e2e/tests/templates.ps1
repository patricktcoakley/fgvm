Set-StrictMode -Version Latest

Suite "export templates" {
    Test "prints grouped template help" {
        $rootHelp = Run "--help"
        $templateHelp = Run "template"
        $templateFlagHelp = Run "template" "--help"
        $templateShortFlagHelp = Run "template" "-h"

        Assert.ExitCode 0 $rootHelp "fgvm --help"
        Assert.Contains "template, t" $rootHelp.Stdout "Root help should show one template command group."
        Assert.NotContains "template install" $rootHelp.Stdout "Root help should not list template subcommands as top-level commands."

        Assert.ExitCode 0 $templateHelp "fgvm template"
        Assert.Contains "Usage: fgvm template <COMMAND>" $templateHelp.Stdout
        Assert.Contains "install, i" $templateHelp.Stdout
        Assert.Contains "list, l" $templateHelp.Stdout
        Assert.Contains "remove, r" $templateHelp.Stdout

        Assert.ExitCode 0 $templateFlagHelp "fgvm template --help"
        Assert.Contains "Usage: fgvm template <COMMAND>" $templateFlagHelp.Stdout

        Assert.ExitCode 0 $templateShortFlagHelp "fgvm template -h"
        Assert.Contains "Usage: fgvm template <COMMAND>" $templateShortFlagHelp.Stdout
    }

    Test "installs lists and removes fixture export templates" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null

        $templatesRoot = Join-Path $Context.RootPath "godot-export-templates"
        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $templatesRoot }
        $standardPath = Join-Path $templatesRoot "4.6.2.stable"
        $monoPath = Join-Path $templatesRoot "4.6.2.stable.mono"
        $standardPayload = Join-Path $standardPath "mock-template.txt"
        $monoPayload = Join-Path $monoPath "mock-template.txt"

        $standardInstall = Run -Environment $environment -Arguments @("template", "install", "4.6.2")
        $monoInstall = Run -Environment $environment -Arguments @("t", "i", "4.6.2", "mono")

        Assert.ExitCode 0 $standardInstall "fgvm template install 4.6.2"
        Assert.ExitCode 0 $monoInstall "fgvm t i 4.6.2 mono"
        Assert.True (Test-Path -LiteralPath $standardPayload -PathType Leaf) "Standard template payload should be extracted."
        Assert.True (Test-Path -LiteralPath $monoPayload -PathType Leaf) "Mono template payload should be extracted."
        Assert.Equal "mock export template for 4.6.2-stable standard" (File.Read $standardPayload)
        Assert.Equal "mock export template for 4.6.2-stable mono" (File.Read $monoPayload)

        $list = Run -Environment $environment -Arguments @("template", "list", "--json")

        Assert.ExitCode 0 $list "fgvm template list --json"
        $templates = Json $list.Stdout
        Assert.Equal @("4.6.2.stable", "4.6.2.stable.mono") @($templates.name | Sort-Object)
        Assert.Equal @("mono", "standard") @($templates.runtime | Sort-Object)

        $removeStandard = Run -Environment $environment -Arguments @("template", "remove", "4.6.2", "standard")
        $removeMono = Run -Environment $environment -Arguments @("t", "r", "4.6.2", "mono")

        Assert.ExitCode 0 $removeStandard "fgvm template remove 4.6.2 standard"
        Assert.ExitCode 0 $removeMono "fgvm t r 4.6.2 mono"
        Assert.False (Test-Path -LiteralPath $standardPath) "Standard template directory should be removed."
        Assert.False (Test-Path -LiteralPath $monoPath) "Mono template directory should be removed."

        $emptyList = Run -Environment $environment -Arguments @("template", "l", "--json")

        Assert.ExitCode 0 $emptyList "fgvm template l --json"
        Assert.Empty (Json $emptyList.Stdout)
    }

    Test "install with templates installs editor and export templates" {
        $templatesRoot = Join-Path $Context.RootPath "godot-export-templates"
        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $templatesRoot }
        $standardPath = Join-Path $templatesRoot "4.6.2.stable"
        $monoPath = Join-Path $templatesRoot "4.6.2.stable.mono"
        $standardPayload = Join-Path $standardPath "mock-template.txt"
        $monoPayload = Join-Path $monoPath "mock-template.txt"

        $standardInstall = Run -Environment $environment -Arguments @("install", "--with-templates", "4.6.2")
        $monoInstall = Run -Environment $environment -Arguments @("install", "--with-templates", "4.6.2", "mono")

        Assert.ExitCode 0 $standardInstall "fgvm install --with-templates 4.6.2"
        Assert.ExitCode 0 $monoInstall "fgvm install --with-templates 4.6.2 mono"
        Assert.True (Test-Path -LiteralPath $standardPayload -PathType Leaf) "Standard template payload should be extracted."
        Assert.True (Test-Path -LiteralPath $monoPayload -PathType Leaf) "Mono template payload should be extracted."

        $list = Run "list" "--json"
        Assert.ExitCode 0 $list "fgvm list --json"
        $installations = Json $list.Stdout
        Assert.ContainsAll @($installations.name) "4.6.2-stable-standard" "4.6.2-stable-mono"

        $templateList = Run -Environment $environment -Arguments @("template", "list", "--json")
        Assert.ExitCode 0 $templateList "fgvm template list --json"
        $templates = Json $templateList.Stdout
        Assert.ContainsAll @($templates.name) "4.6.2.stable" "4.6.2.stable.mono"
    }
}
