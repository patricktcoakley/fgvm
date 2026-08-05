Set-StrictMode -Version Latest

# ConsoleAppFramework has deficiencies when mixing `[Argument] params string[]` with flags so these tests are here
# to just make sure commands don't drift.

class QueryOptionCase {
    [string] $Name
    [scriptblock] $BuildEnvironment = { @{} }
    [scriptblock] $Setup = { param($environment) }
    [string[]] $OptionBeforeQuery
    [string[]] $OptionAfterQuery
    [scriptblock] $Observe
    [string] $WhenApplied
    [string] $WhenIgnored
}

function TemplatesRoot { Join-Path $Context.RootPath "godot-export-templates" }
function MonoTemplate { Join-Path (TemplatesRoot) "4.6.2.stable.mono" }
function MonoTemplatePayload { Join-Path (MonoTemplate) "mock-template.txt" }

$queryOptionCases = @(
    [QueryOptionCase]@{
        Name              = "search --json"
        OptionBeforeQuery = @("search", "--json", "4.6")
        OptionAfterQuery  = @("search", "4.6", "--json")
        Observe           = {
            param($result)
            if ($result.Stdout.TrimStart().StartsWith("[")) { "json" } else { "columns" }
        }
        WhenApplied       = "json"
        WhenIgnored       = "columns"
    }
    [QueryOptionCase]@{
        Name              = "install --default"
        Setup             = {
            param($environment)
            Add-FixtureInstallation "4.6.2-stable" -Default | Out-Null
        }
        OptionBeforeQuery = @("install", "--default", "4.6.2", "mono")
        OptionAfterQuery  = @("install", "4.6.2", "mono", "--default")
        Observe           = {
            param($result)
            ((Manifest.From $Context.InstallationsPath)["default"] -split "@")[0]
        }
        WhenApplied       = "4.6.2-stable-mono"
        WhenIgnored       = "4.6.2-stable-standard"
    }
    [QueryOptionCase]@{
        Name              = "remove --with-templates"
        BuildEnvironment  = { @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = (TemplatesRoot) } }
        Setup             = {
            param($environment)
            Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null
            Run -Environment $environment -Arguments @("template", "install", "4.6.2", "mono") | Out-Null
        }
        OptionBeforeQuery = @("remove", "--with-templates", "4.6.2", "mono")
        OptionAfterQuery  = @("remove", "4.6.2", "mono", "--with-templates")
        Observe           = {
            param($result)
            if (Test-Path -LiteralPath (MonoTemplate)) { "kept" } else { "removed" }
        }
        WhenApplied       = "removed"
        WhenIgnored       = "kept"
    }
    [QueryOptionCase]@{
        Name              = "template install --force"
        BuildEnvironment  = { @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = (TemplatesRoot) } }
        Setup             = {
            param($environment)
            Add-FixtureInstallation "4.6.2-stable" "mono" | Out-Null
            Run -Environment $environment -Arguments @("template", "install", "4.6.2", "mono") | Out-Null
            Set-Content -LiteralPath (MonoTemplatePayload) -Value "tampered"
        }
        OptionBeforeQuery = @("template", "install", "--force", "4.6.2", "mono")
        OptionAfterQuery  = @("template", "install", "4.6.2", "mono", "--force")
        Observe           = {
            param($result)
            if ((File.Read (MonoTemplatePayload)).Trim() -eq "tampered") { "tampered" } else { "restored" }
        }
        WhenApplied       = "restored"
        WhenIgnored       = "tampered"
    }
)

Suite "query options" {
    foreach ($case in $queryOptionCases) {
        Test "$($case.Name) applies when it comes before the query" {
            $environment = & $case.BuildEnvironment
            & $case.Setup $environment

            $result = Run -Environment $environment -Arguments $case.OptionBeforeQuery

            Assert.ExitCode 0 $result "fgvm $($case.OptionBeforeQuery -join ' ')"
            Assert.Equal $case.WhenApplied (& $case.Observe $result) `
                "The option should apply when it comes before the query."
        }

        Test "$($case.Name) is inert when it trails the query" {
            $environment = & $case.BuildEnvironment
            & $case.Setup $environment

            $result = Run -Environment $environment -Arguments $case.OptionAfterQuery

            Assert.Equal $case.WhenIgnored (& $case.Observe $result) `
                "An option after the query is read as a query word and must not take effect."
        }
    }

    Test "both query words narrow the result" {
        $all = Run "search" "--json" "4.6"
        $filtered = Run "search" "--json" "4.6" "rc"

        Assert.ExitCode 0 $filtered "fgvm search --json 4.6 rc"
        Assert.Equal @("4.6.2-stable", "4.6.2-rc2") @((Json $all.Stdout).name)
        Assert.Equal @("4.6.2-rc2") @((Json $filtered.Stdout).name)
    }
}
