Set-StrictMode -Version Latest

Suite "release catalog" {
    $freshCacheManifest = [ordered]@{
        lastUpdated = "2999-01-01T00:00:00+00:00"
        releases = [ordered]@{
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

    Test "search uses a fresh releases json without rewriting it" {
        $seededTimestamp = [datetime]::SpecifyKind([datetime]::Parse("2025-01-01T00:00:00"), [System.DateTimeKind]::Utc)
        Manifest.Write $Context.ReleasesPath $freshCacheManifest $seededTimestamp

        $cached = Run "search" "--json" "4.999"

        Assert.ExitCode 0 $cached "fgvm search --json should use a fresh cache."
        Assert.Contains "4.999-stable" $cached.Stdout
        Assert.Equal $seededTimestamp (File.ModifiedAt $Context.ReleasesPath) "A fresh cache should not be rewritten."
    }

    Test "search no-cache refreshes a seeded releases json" {
        $seededTimestamp = [datetime]::SpecifyKind([datetime]::Parse("2025-01-01T00:00:00"), [System.DateTimeKind]::Utc)
        Manifest.Write $Context.ReleasesPath $freshCacheManifest $seededTimestamp

        $refreshed = Run "search" "--no-cache" "--json" "4.999"

        Assert.ExitCode 0 $refreshed "fgvm search --no-cache --json should refresh releases.json."
        Assert.NotContains "4.999-stable" $refreshed.Stdout
        Assert.True ((File.ModifiedAt $Context.ReleasesPath) -gt $seededTimestamp) "A no-cache search should rewrite releases.json."
        Assert.NotContains "4.999" (File.Read $Context.ReleasesPath)
    }
}
