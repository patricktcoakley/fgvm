Set-StrictMode -Version Latest

Suite "removal" {
    Test "removes an exact seeded version and preserves unrelated versions" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $older = Add-FixtureInstallation "4.5-stable"

        $remove = Run "remove" "4.5"
        $list = Run "list" "--json"

        Assert.ExitCode 0 $remove "fgvm remove 4.5"
        Assert.False (Test-Path -LiteralPath $older.InstallationPath) "The selected installation directory should be removed."
        Assert.True (Test-Path -LiteralPath $stable.InstallationPath) "Unrelated installations should remain."
        Assert.Equal @($stable.Name) @((Json $list.Stdout).name)
    }

    Test "removes only the requested runtime" {
        $standard = Add-FixtureInstallation "4.6.2-stable" -Default
        $mono = Add-FixtureInstallation "4.6.2-stable" "mono"

        $remove = Run "remove" "4.6" "mono"
        $list = Run "list" "--json"

        Assert.ExitCode 0 $remove "fgvm remove 4.6 mono"
        Assert.False (Test-Path -LiteralPath $mono.InstallationPath)
        Assert.True (Test-Path -LiteralPath $standard.InstallationPath)
        Assert.Equal @($standard.Name) @((Json $list.Stdout).name)
    }

    Test "leaves installations unchanged when no query matches" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default

        $remove = Run "remove" "9.999"
        $list = Run "list" "--json"

        Assert.ExitCode 0 $remove "fgvm remove 9.999"
        Assert.Contains "Couldn't find any versions" $remove.Stdout
        Assert.True (Test-Path -LiteralPath $stable.InstallationPath)
        Assert.Equal @($stable.Name) @((Json $list.Stdout).name)
    }

    Test "clears the default when removing it" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $older = Add-FixtureInstallation "4.5-stable"

        $remove = Run "remove" "4.6"

        Assert.ExitCode 0 $remove "fgvm remove default"
        $registry = Manifest.From $Context.InstallationsPath
        Assert.Equal $null $registry["default"]
        Assert.NotContains $stable.Key $registry["installations"].Keys
        Assert.Contains $older.Key $registry["installations"].Keys
    }

    Test "removes the final version and reaches an empty state" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default

        $remove = Run "remove" "4.6"
        $list = Run "list" "--json"

        Assert.ExitCode 0 $remove "fgvm remove final version"
        Assert.Empty (Json $list.Stdout)
        Assert.False (Test-Path -LiteralPath $stable.InstallationPath)
        $registry = Manifest.From $Context.InstallationsPath
        Assert.Equal $null $registry["default"]
        Assert.Empty $registry["installations"].Keys
    }

    Test "handles removal from a fresh home" {
        $remove = Run "remove" "4.6"

        Assert.ExitCode 0 $remove "fgvm remove with no installations"
        Assert.Contains "No installations" $remove.Stdout
    }

    Test "cleans up after a removal that was interrupted" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $leftover = Join-Path $Context.InstallationsDirectoryPath "4.5-stable" ".fgvm-removing-00000000000000000000000000000000-macos.universal"
        New-Item -ItemType Directory -Path $leftover -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $leftover "godot") -Value "half-deleted"

        $list = Run "list" "--json"

        # The leftover is swept, and the installation that is actually present is untouched.
        Assert.ExitCode 0 $list "fgvm list with an interrupted removal left behind"
        Assert.False (Test-Path -LiteralPath $leftover)
        Assert.True (Test-Path -LiteralPath $stable.InstallationPath)
        Assert.Equal @($stable.Name) @((Json $list.Stdout).name)
    }

    Test "removes case-variant paths according to host filesystem semantics" {
        $installation = Add-FixtureInstallation "4.6.2-stable"
        $lowerParent = Join-Path $Context.FgvmRootPath "case-root"
        $upperParent = Join-Path $Context.FgvmRootPath "CASE-ROOT"
        $editorPath = Join-Path $lowerParent "4.6.2.stable"
        $templatePath = Join-Path $upperParent "4.6.2.stable"

        New-Item -ItemType Directory -Path $lowerParent -Force | Out-Null
        Move-Item -LiteralPath $installation.InstallationPath -Destination $editorPath

        $registry = Manifest.From $Context.InstallationsPath
        $registry["installations"][$installation.Key]["path"] = "case-root/4.6.2.stable"
        Manifest.Write $Context.InstallationsPath $registry

        # These are distinct directories on a case-sensitive volume and aliases everywhere else.
        New-Item -ItemType Directory -Path $templatePath -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $templatePath "template.txt") -Value "template"
        $parentNames = @(Get-ChildItem -LiteralPath $Context.FgvmRootPath -Directory -Force | ForEach-Object Name)
        $filesystemMode = if ($parentNames -ccontains "case-root" -and $parentNames -ccontains "CASE-ROOT") {
            "case-distinct"
        }
        else {
            "case-aliased"
        }

        $environment = @{ FGVM_GODOT_EXPORT_TEMPLATES_DIR = $upperParent }
        $remove = Run -Environment $environment -Arguments @("remove", "--with-templates", "4.6.2")

        Assert.ExitCode 0 $remove "fgvm remove case-variant paths ($filesystemMode)"
        Assert.False (Test-Path -LiteralPath $editorPath) "The editor path should be removed ($filesystemMode)."
        Assert.False (Test-Path -LiteralPath $templatePath) "The template path should be removed ($filesystemMode)."
        $tombstones = @(Get-ChildItem -LiteralPath $Context.FgvmRootPath -Directory -Recurse -Force |
                Where-Object Name -Like ".fgvm-removing-*")
        Assert.Empty $tombstones "No removal tombstones should remain ($filesystemMode)."
    }
}
