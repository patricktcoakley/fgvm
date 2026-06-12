Set-StrictMode -Version Latest

Suite "workflow" {
    Test "invalid operations do not install anything" {
        $install = Run "install" "nonexistent-version-999"
        $local = Run "local" "nonexistent-version-999"
        $set = Run "set" "nonexistent-version-999"

        Assert.ExitCode 2 $install "fgvm install should reject an unknown version."
        Assert.ExitCode 2 $local "fgvm local should reject an unknown version."
        Assert.ExitCode 1 $set "fgvm set should reject an unknown version."

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list --json"
        Assert.Empty (Json $list.Stdout) "Invalid operations should not install versions."
    }

    Test "selects a seeded version with a partial query" {
        $stable = Add-FixtureInstallation "4.6.2-stable"
        $older = Add-FixtureInstallation "4.5-stable" -Default

        $set = Run "set" "4.6"
        $which = Run "which" "--json"

        Assert.ExitCode 0 $set "fgvm set 4.6"
        Assert.Contains "4.6.2-stable-standard" $set.Stdout
        Assert.ExitCode 0 $which "fgvm which --json"

        $selected = Json $which.Stdout
        Assert.True $selected.hasVersion
        Assert.Equal $stable.ExecutablePath $selected.executablePath
        Assert.NotEqual $older.ExecutablePath $selected.executablePath
    }

    Test "selects a seeded mono runtime" {
        $standard = Add-FixtureInstallation "4.6.2-stable" -Default
        $mono = Add-FixtureInstallation "4.6.2-stable" "mono"

        $set = Run "set" "4.6" "mono"
        $which = Run "which" "--json"

        Assert.ExitCode 0 $set "fgvm set 4.6 mono"
        Assert.ExitCode 0 $which "fgvm which --json"
        Assert.Equal $mono.ExecutablePath (Json $which.Stdout).executablePath
        Assert.NotEqual $standard.ExecutablePath (Json $which.Stdout).executablePath
    }

    Test "keeps the current default when a set query does not match" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default

        $set = Run "set" "9.999"
        $which = Run "which" "--json"

        Assert.ExitCode 1 $set "fgvm set 9.999"
        Assert.Equal $stable.ExecutablePath (Json $which.Stdout).executablePath
    }
}
