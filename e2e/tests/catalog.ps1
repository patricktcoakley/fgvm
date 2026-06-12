Set-StrictMode -Version Latest

Suite "release catalog" {
    $freshCacheManifest = [ordered]@{
        lastUpdated = "2999-01-01T00:00:00+00:00"
        releases    = [ordered]@{
            "4.999" = [ordered]@{
                stable = [ordered]@{}
            }
        }
    }

    Test "search reads fixtures and writes releases json" {
        $search = Run "search" "--json" "4.6"

        Assert.ExitCode 0 $search "fgvm search --json 4.6"

        $releases = @(Json $search.Stdout)
        $names = @($releases | ForEach-Object { $_.name })

        Assert.ContainsAll $names "4.6.2-stable" "4.6.2-rc2"

        Assert.True (File.Exists $Context.ReleasesPath) "search should write releases.json."

        $manifest = Manifest.From $Context.ReleasesPath

        Assert.True ($null -ne $manifest["lastUpdated"]) "releases.json should include lastUpdated."
        Assert.Contains "4.6.2" $manifest["releases"].Keys "releases.json should include fixture release metadata."
    }

    Test "search filters fixture releases and returns an empty array for no matches" {
        $filtered = Run "search" "--json" "4.6"
        $missing = Run "search" "--json" "9.999"

        Assert.ExitCode 0 $filtered "fgvm search --json 4.6"
        $filteredNames = @((Json $filtered.Stdout) | ForEach-Object { $_.name })
        Assert.Equal @("4.6.2-stable", "4.6.2-rc2") $filteredNames

        Assert.ExitCode 0 $missing "fgvm search --json 9.999"
        Assert.Empty (Json $missing.Stdout)
    }

    Test "search preserves fixture release ordering" {
        $search = Run "search" "--no-cache" "--json"

        Assert.ExitCode 0 $search "fgvm search --no-cache --json"
        $names = @((Json $search.Stdout) | ForEach-Object { $_.name })

        Assert.Equal @("4.7-dev1", "4.6.2-stable", "4.6.2-rc2", "4.5-stable", "3.6.1-stable") $names
    }

    Test "search rebuilds a malformed releases json" {
        New-Item -ItemType Directory -Path $Context.FgvmRootPath -Force | Out-Null
        Set-Content -LiteralPath $Context.ReleasesPath -Value "{ not-json" -NoNewline

        $search = Run "search" "--json" "4.6"

        Assert.ExitCode 0 $search "fgvm search with malformed releases.json"
        $manifest = Manifest.From $Context.ReleasesPath
        Assert.Contains "4.6.2" $manifest["releases"].Keys
    }

    Test "search uses a fresh releases json without rewriting it" {
        $seededTimestamp = [datetime]::SpecifyKind([datetime]::Parse("2025-01-01T00:00:00"), [System.DateTimeKind]::Utc)
        Manifest.Write $Context.ReleasesPath $freshCacheManifest $seededTimestamp

        $cached = Run "search" "--json" "4.999"

        Assert.ExitCode 0 $cached "fgvm search --json should use a fresh cache."
        Assert.Contains "4.999-stable" $cached.Stdout
    }

    Test "search no-cache refreshes a seeded releases json" {
        $seededTimestamp = [datetime]::SpecifyKind([datetime]::Parse("2025-01-01T00:00:00"), [System.DateTimeKind]::Utc)
        Manifest.Write $Context.ReleasesPath $freshCacheManifest $seededTimestamp

        $refreshed = Run "search" "--no-cache" "--json" "4.999"

        Assert.ExitCode 0 $refreshed "fgvm search --no-cache --json should refresh releases.json."
        Assert.NotContains "4.999-stable" $refreshed.Stdout
        Assert.NotContains "4.999" (File.Read $Context.ReleasesPath)
    }

    Test "search falls back to a stale cache when fixture refresh fails" {
        $prime = Run "search" "--no-cache" "--json" "4.6"
        Assert.ExitCode 0 $prime "prime releases.json"

        $manifest = Manifest.From $Context.ReleasesPath
        $manifest["lastUpdated"] = "2000-01-01T00:00:00+00:00"
        Manifest.Write $Context.ReleasesPath $manifest

        $missingManifest = Join-Path $Context.RootPath "missing-fixture.json"
        $fallback = Run -Environment @{ FGVM_INTEGRATION_FIXTURE_MANIFEST = $missingManifest } `
            -Arguments @("search", "4.6")

        Assert.ExitCode 0 $fallback "fgvm search with failed fixture refresh"
        Assert.Contains "Could not refresh the release cache" $fallback.Stdout
        Assert.ContainsAll $fallback.Stdout "4.6.2-stable" "4.6.2-rc2"
    }

    Test "search fails when fixture refresh and cache are unavailable" {
        $missingManifest = Join-Path $Context.RootPath "missing-fixture.json"

        $search = Run -Environment @{ FGVM_INTEGRATION_FIXTURE_MANIFEST = $missingManifest } `
            -Arguments @("search", "--json", "4.6")

        Assert.ExitCode 1 $search "fgvm search without fixture or cache"
        Assert.Contains "Something went wrong" $search.Stdout
        Assert.False (File.Exists $Context.ReleasesPath)
    }
}
