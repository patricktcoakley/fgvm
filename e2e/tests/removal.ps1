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
}
