Set-StrictMode -Version Latest

Suite "installation registry" {
    Test "discovers a filesystem-only installation" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        Remove-Item -LiteralPath $Context.InstallationsPath -Force

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list --json"
        $installed = @(Json $list.Stdout)
        Assert.Equal 1 $installed.Count
        Assert.Equal $seeded.Name $installed[0].name

        $registry = Manifest.From $Context.InstallationsPath
        Assert.Contains $seeded.Key $registry["installations"].Keys
    }

    Test "rebuilds a malformed registry from installation folders" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        Set-Content -LiteralPath $Context.InstallationsPath -Value "{ not-json" -NoNewline

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list with malformed installations.json"
        Assert.Equal $seeded.Name (@(Json $list.Stdout)[0].name)
        Assert.Contains $seeded.Key (Manifest.From $Context.InstallationsPath)["installations"].Keys
    }

    Test "drops unsafe records and clears their default" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        $registry = Manifest.From $Context.InstallationsPath
        $unsafeKey = "4.5-stable-standard@$($seeded.Target)"
        $registry["installations"][$unsafeKey] = [ordered]@{
            path           = "../outside"
            installedAt    = $null
            lastLaunchedAt = $null
        }
        $registry["default"] = $unsafeKey
        Manifest.Write $Context.InstallationsPath $registry

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list with unsafe registry entry"
        $rebuilt = Manifest.From $Context.InstallationsPath
        Assert.Equal $null $rebuilt["default"]
        Assert.Equal 1 $rebuilt["installations"].Count
        Assert.Contains $seeded.Key $rebuilt["installations"].Keys
        Assert.NotContains $unsafeKey $rebuilt["installations"].Keys
    }

    Test "preserves a valid default while regenerating stale records" {
        $stable = Add-FixtureInstallation "4.6.2-stable" -Default
        $older = Add-FixtureInstallation "4.5-stable"
        $registry = Manifest.From $Context.InstallationsPath
        $staleKey = "3.6.1-stable-standard@$($stable.Target)"
        $registry["installations"][$staleKey] = [ordered]@{
            path           = "installations/3.6.1-stable-standard/$($stable.Target)"
            installedAt    = $null
            lastLaunchedAt = $null
        }
        Manifest.Write $Context.InstallationsPath $registry

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list with stale registry entry"
        $rebuilt = Manifest.From $Context.InstallationsPath
        Assert.Equal $stable.Key $rebuilt["default"]
        Assert.ContainsAll $rebuilt["installations"].Keys $stable.Key $older.Key
        Assert.NotContains $staleKey $rebuilt["installations"].Keys
    }

    Test "imports and launches a legacy root installation" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        $legacyPath = Join-Path $Context.FgvmRootPath $seeded.Name
        $executableRelativePath = [System.IO.Path]::GetRelativePath($seeded.InstallationPath, $seeded.ExecutablePath)
        Move-Item -LiteralPath $seeded.InstallationPath -Destination $legacyPath
        Remove-Item -LiteralPath $Context.InstallationsPath -Force

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list with a legacy installation"
        Assert.Equal @($seeded.Name) @((Json $list.Stdout).name)
        $registry = Manifest.From $Context.InstallationsPath
        Assert.Equal $seeded.Name $registry["installations"][$seeded.Key]["path"]

        Assert.ExitCode 0 (Run "set" "4.6") "fgvm set legacy installation"
        $godot = Run "godot" "--attached" "--args" "--version"
        Assert.ExitCode 0 $godot "fgvm godot with legacy installation"
        $mockVersion = (Manifest.From $Context.FixtureManifestPath)["mockVersion"]
        Assert.Equal $mockVersion $godot.Stdout.Trim()
        Assert.True (Test-Path -LiteralPath (Join-Path $legacyPath $executableRelativePath) -PathType Leaf)
    }

    Test "prefers target-aware layout when the same legacy installation exists" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        $legacyPath = Join-Path $Context.FgvmRootPath $seeded.Name
        Copy-Item -LiteralPath $seeded.InstallationPath -Destination $legacyPath -Recurse
        Remove-Item -LiteralPath $Context.InstallationsPath -Force

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list with duplicate layouts"
        Assert.Equal 1 (Json $list.Stdout).Count
        $registry = Manifest.From $Context.InstallationsPath
        Assert.Equal $seeded.RelativePath $registry["installations"][$seeded.Key]["path"]
    }

    Test "infers the default from an existing selected-version artifact" {
        $seeded = Add-FixtureInstallation "4.6.2-stable"
        Assert.ExitCode 0 (Run "set" "4.6") "fgvm set 4.6"
        Remove-Item -LiteralPath $Context.InstallationsPath -Force

        $list = Run "list" "--json"

        Assert.ExitCode 0 $list "fgvm list while rebuilding default"
        $registry = Manifest.From $Context.InstallationsPath
        Assert.Equal $seeded.Key $registry["default"]
        Assert.True (@((Json $list.Stdout) | Where-Object { $_.isDefault }).Count -eq 1)
    }
}
