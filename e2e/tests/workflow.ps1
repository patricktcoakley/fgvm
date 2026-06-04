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

    Test "installs launches and removes a default version" {
        $version = "4.6.2-stable"
        $releaseName = "4.6.2-stable-standard"

        Step "install default version" {
            $install = Run "install" $version

            Assert.ExitCode 0 $install "fgvm install $version"

            $list = Run "list" "--json"

            Assert.ExitCode 0 $list "fgvm list --json"
            $installed = @(Json $list.Stdout)

            Assert.Equal 1 $installed.Count
            Assert.Equal $releaseName $installed[0].name
            Assert.True $installed[0].isDefault "The first installed version should become default."
        }

        Step "launch selected version" {
            $which = Run "which" "--json"

            Assert.ExitCode 0 $which "fgvm which --json"
            $selected = Json $which.Stdout

            Assert.True $selected.hasVersion "which should report a selected version after install."
            Assert.True (Test-Path -LiteralPath $selected.executablePath -PathType Leaf) "which should point at the installed Godot executable."

            $godot = Run "godot" "--args" "--version"

            Assert.ExitCode 0 $godot "fgvm godot --args --version"
            Assert.Contains "4.6.2.stable.standard.mock" $godot.Stdout "fgvm godot should launch the fixture executable."
        }

        Step "registry records default" {
            $registry = Manifest.From $Context.InstallationsPath
            $keys = @($registry["installations"].Keys)

            Assert.Equal 1 $keys.Count
            Assert.Contains "$releaseName@" $keys[0]
            Assert.Equal $keys[0] $registry["default"]
        }

        Step "remove version" {
            $remove = Run "remove" $version

            Assert.ExitCode 0 $remove "fgvm remove $version"

            $after = Run "list" "--json"

            Assert.ExitCode 0 $after "fgvm list --json after remove"
            Assert.Empty (Json $after.Stdout) "remove should clear the installed version."

            $registryAfterRemove = Manifest.From $Context.InstallationsPath

            Assert.Equal $null $registryAfterRemove["default"]
            Assert.Empty $registryAfterRemove["installations"].Keys
        }
    }

    Test "writes a local version file in the working directory" {
        $install = Run "install" "4.5-stable"
        Assert.ExitCode 0 $install "fgvm install 4.5-stable"

        $projectPath = Join-Path $Context.WorkPath "project"
        New-Item -ItemType Directory -Path $projectPath -Force | Out-Null

        $local = Run -Cwd $projectPath "local" "4.5"

        Assert.ExitCode 0 $local "fgvm local 4.5"

        $versionPath = Join-Path $projectPath ".fgvm-version"
        Assert.True (Test-Path -LiteralPath $versionPath -PathType Leaf) "fgvm local should write .fgvm-version."
        Assert.Equal "4.5-stable-standard" (Get-Content -LiteralPath $versionPath -Raw).Trim()
    }
}
