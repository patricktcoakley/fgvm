Set-StrictMode -Version Latest

Suite "query options" {
    Test "search applies its option and both query words" {
        $all = Run "search" "--json" "4.6"
        $filtered = Run "search" "--json" "4.6" "rc"

        Assert.ExitCode 0 $filtered "fgvm search --json 4.6 rc"
        Assert.Equal @("4.6.2-stable", "4.6.2-rc2") @((Json $all.Stdout).name)
        Assert.Equal @("4.6.2-rc2") @((Json $filtered.Stdout).name)
    }

    Test "install applies its option and both query words" {
        $install = Run "install" "--default" "4.6.2" "mono"

        Assert.ExitCode 0 $install "fgvm install --default 4.6.2 mono"

        $list = Run "list" "--json"
        Assert.Equal @("4.6.2-stable-mono") @((Json $list.Stdout).name)

        $registry = Manifest.From $Context.InstallationsPath
        Assert.Contains "4.6.2-stable-mono" $registry["default"]
    }

    Test "remove applies its option and both query words" {
        Add-FixtureInstallation "4.6.2-stable" | Out-Null
        Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null

        $templatesRoot = Join-Path $Context.RootPath "godot-export-templates"
        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $templatesRoot }
        $monoTemplate = Join-Path $templatesRoot "4.6.2.stable.mono"

        Run -Environment $environment -Arguments @("template", "install", "4.6.2", "mono") | Out-Null
        Assert.True (Test-Path -LiteralPath $monoTemplate) "The mono template should be installed before removal."

        $remove = Run -Environment $environment -Arguments @("remove", "--with-templates", "4.6.2", "mono")

        Assert.ExitCode 0 $remove "fgvm remove --with-templates 4.6.2 mono"
        Assert.Equal @("4.6.2-stable-standard") @((Json (Run "list" "--json").Stdout).name)
        Assert.False (Test-Path -LiteralPath $monoTemplate) "--with-templates should have removed the mono template."
    }

    Test "template install applies its option and both query words" {
        Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null

        $templatesRoot = Join-Path $Context.RootPath "godot-export-templates"
        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $templatesRoot }
        $payload = Join-Path (Join-Path $templatesRoot "4.6.2.stable.mono") "mock-template.txt"

        Run -Environment $environment -Arguments @("template", "install", "4.6.2", "mono") | Out-Null
        $original = File.Read $payload
        Set-Content -LiteralPath $payload -Value "tampered"

        $forced = Run -Environment $environment -Arguments @("template", "install", "--force", "4.6.2", "mono")

        Assert.ExitCode 0 $forced "fgvm template install --force 4.6.2 mono"
        Assert.Equal $original (File.Read $payload) "--force should have replaced the tampered payload."
    }
}
