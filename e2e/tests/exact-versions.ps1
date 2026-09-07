Set-StrictMode -Version Latest

function Use-ComparisonFixtures {
    $Context.FixtureManifestPath = Join-Path (Split-Path -Parent $Context.FixtureManifestPath) "comparison-manifest.json"
}

Suite "exact versions" {
    foreach ($runtime in @("standard", "mono")) {
        Test "exact $runtime stable release stays pinned while fuzzy queries select a newer patch" {
            Use-ComparisonFixtures
            $fuzzy = Run "install" "4.5" $runtime

            Assert.ExitCode 0 $fuzzy "fuzzy install with competing patches"
            $fuzzyList = Run "list" "--json"
            Assert.ExitCode 0 $fuzzyList "list after fuzzy install"
            Assert.Equal @("4.5.1-stable-$runtime") @((Json $fuzzyList.Stdout).name)
            $exact = Run "install" "4.5-stable-$runtime"

            Assert.ExitCode 0 $exact "exact install with competing patches"
            $list = Run "list" "--json"
            Assert.ExitCode 0 $list "fgvm list --json"
            $installed = Json $list.Stdout
            Assert.Equal 2 $installed.Count
            Assert.ContainsAll @($installed.name) "4.5-stable-$runtime" "4.5.1-stable-$runtime"

            $projectPath = Join-Path $Context.WorkPath "fuzzy-local"
            New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
            $local = Run -Cwd $projectPath -Arguments @("local", "4.5", "stable", $runtime)

            Assert.ExitCode 0 $local "fuzzy local resolves before writing the pin"
            Assert.Equal "4.5.1-stable-$runtime" (File.Read (Join-Path $projectPath ".fgvm-version")).Trim()
        }

        foreach ($release in @(
            @{ Version = "4.7"; Type = "dev"; Exact = "dev1"; Latest = "dev10" },
            @{ Version = "4.6.2"; Type = "rc"; Exact = "rc2"; Latest = "rc20" }
        )) {
            Test "exact $runtime $($release.Exact) does not expand to $($release.Latest)" {
                Use-ComparisonFixtures
                $exactName = "$($release.Version)-$($release.Exact)-$runtime"
                $latestName = "$($release.Version)-$($release.Latest)-$runtime"
                $fuzzy = Run "install" $release.Version $release.Type $runtime

                Assert.ExitCode 0 $fuzzy "fuzzy prerelease install"
                $fuzzyList = Run "list" "--json"
                Assert.ExitCode 0 $fuzzyList "list after fuzzy prerelease install"
                Assert.Equal @($latestName) @((Json $fuzzyList.Stdout).name)
                $exact = Run "install" $exactName

                Assert.ExitCode 0 $exact "exact prerelease install"
                $list = Run "list" "--json"
                Assert.ExitCode 0 $list "fgvm list --json"
                $installed = Json $list.Stdout
                Assert.Equal 2 $installed.Count
                Assert.ContainsAll @($installed.name) $exactName $latestName
            }
        }

        Test "missing $runtime pin installs and launches only that release despite a newer default" {
            Use-ComparisonFixtures
            $newer = Add-FixtureInstallation "4.5.1-stable" $runtime -Default
            $projectPath = Join-Path $Context.WorkPath "pinned-project"
            New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
            $versionPath = Join-Path $projectPath ".fgvm-version"
            $pin = "4.5-stable-$runtime"
            Set-Content -LiteralPath $versionPath -Value $pin -NoNewline

            foreach ($arguments in @(@("which"), @("godot", "--attached", "--args", "--fgvm-mock-print-directory"))) {
                $missing = Run -Cwd $projectPath -Arguments $arguments

                Assert.ExitCode 1 $missing "missing pin must not use the newer installed patch"
                Assert.Equal "" $missing.Stdout.Trim()
                Assert.Equal $pin (File.Read $versionPath)
                Assert.False (File.Exists $newer.MockInvocationPath)
            }

            $local = Run -Cwd $projectPath "local"

            Assert.ExitCode 0 $local "install the existing pin"
            Assert.Equal $pin (File.Read $versionPath).Trim()
            $firstPin = File.Read $versionPath
            $which = Run -Cwd $projectPath "which"
            $godot = Run -Cwd $projectPath "godot" "--attached" "--args" "--fgvm-mock-print-directory"
            $repeat = Run -Cwd $projectPath "local"

            Assert.ExitCode 0 $which "resolve the exact pin"
            Assert.Contains $pin $which.Stdout
            Assert.NotContains $newer.Name $which.Stdout
            Assert.ExitCode 0 $godot "launch the exact pin"
            Assert.Contains $pin (($godot.Stdout -replace "\s+", " ").Trim())
            Assert.NotContains $newer.Name $godot.Stdout
            Assert.False (File.Exists $newer.MockInvocationPath)
            Assert.ExitCode 0 $repeat "repeat local with both releases installed"
            Assert.Equal $firstPin (File.Read $versionPath)

            $list = Run "list" "--json"
            Assert.ExitCode 0 $list "fgvm list --json"
            $installed = Json $list.Stdout
            Assert.Equal 2 $installed.Count
            Assert.ContainsAll @($installed.name) $pin $newer.Name
            Assert.Equal $newer.Key (Manifest.From $Context.InstallationsPath)["default"]
        }

        Test "$runtime pins accept casing and whitespace without rewriting on read" {
            Use-ComparisonFixtures
            $newer = Add-FixtureInstallation "4.5.1-stable" $runtime -Default
            $exact = Add-FixtureInstallation "4.5-stable" $runtime
            $projectPath = Join-Path $Context.WorkPath "formatted-pin"
            New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
            $versionPath = Join-Path $projectPath ".fgvm-version"
            $pin = "`t$($exact.Name.ToUpperInvariant())`r`n"
            Set-Content -LiteralPath $versionPath -Value $pin -NoNewline

            $which = Run -Cwd $projectPath "which"
            $godot = Run -Cwd $projectPath "godot" "--attached" "--args" "--fgvm-mock-print-directory"

            Assert.ExitCode 0 $which "case-insensitive pin lookup"
            Assert.Equal ([System.IO.Path]::GetFullPath($exact.ExecutablePath)) ([System.IO.Path]::GetFullPath($which.Stdout.Trim()))
            Assert.ExitCode 0 $godot "launch a whitespace-padded pin"
            Assert.True (File.Exists $exact.MockInvocationPath)
            Assert.False (File.Exists $newer.MockInvocationPath)
            Assert.Equal $pin (File.Read $versionPath)
        }

        foreach ($failure in @("checksum", "missing-download")) {
            Test "failed $runtime exact $failure installation preserves the pin and installed alternatives" {
                Use-ComparisonFixtures
                $newer = Add-FixtureInstallation "4.5.1-stable" $runtime -Default
                $projectPath = Join-Path $Context.WorkPath "failed-pin"
                New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
                $versionPath = Join-Path $projectPath ".fgvm-version"
                $pin = "4.5-stable-$runtime"
                Set-Content -LiteralPath $versionPath -Value $pin -NoNewline

                $fixtureRoot = Split-Path -Parent $Context.FixtureManifestPath
                $manifest = Manifest.From $Context.FixtureManifestPath
                $failedZip = Join-Path $Context.WorkPath "failed-download.zip"
                if ($failure -eq "checksum") {
                    Set-Content -LiteralPath $failedZip -Value "corrupted download" -NoNewline
                }
                foreach ($artifact in $manifest["artifacts"]) {
                    $artifact["zipPath"] = [System.IO.Path]::GetFullPath($artifact["zipPath"], $fixtureRoot)
                    if ($artifact["releaseName"] -eq "4.5-stable" -and $artifact["runtime"] -eq $runtime) {
                        $artifact["zipPath"] = $failedZip
                    }
                }
                $failedManifest = Join-Path $Context.WorkPath "failed-manifest.json"
                Manifest.Write $failedManifest $manifest
                $environment = @{ FGVM_INTEGRATION_FIXTURE_MANIFEST = $failedManifest }

                foreach ($arguments in @(@("install", $pin), @("local", $pin), @("local"))) {
                    $logOffset = if (File.Exists $Context.LogPath) { (File.Read $Context.LogPath).Length } else { 0 }
                    $result = Run -Cwd $projectPath -Environment $environment -Arguments $arguments
                    $output = (($result.Stdout + $result.Stderr) -replace "\s+", " ").Trim()
                    $diagnostics = $output + (File.Read $Context.LogPath).Substring($logOffset)

                    Assert.ExitCode 1 $result "failed exact installation"
                    Assert.Contains $(if ($failure -eq "checksum") { "Checksum mismatch" } else { "Failed to copy fixture zip" }) $diagnostics
                    Assert.NotContains "Choose from installed" $output
                    Assert.NotContains "cannot prompt" $output
                    Assert.Equal $pin (File.Read $versionPath)
                    Assert.False (File.Exists $newer.MockInvocationPath)
                    $list = Run "list" "--json"
                    Assert.ExitCode 0 $list "list after failed installation"
                    Assert.Equal @($newer.Name) @((Json $list.Stdout).name)
                    Assert.Equal $newer.Key (Manifest.From $Context.InstallationsPath)["default"]
                }
            }
        }

        Test "$runtime project patch requirements remain lower bounds rather than exact pins" {
            Use-ComparisonFixtures
            $newer = Add-FixtureInstallation "4.5.1-stable" $runtime
            $projectPath = Join-Path $Context.WorkPath "project-patch"
            New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
            $projectFile = Join-Path $projectPath "project.godot"
            $versionPath = Join-Path $projectPath ".fgvm-version"
            $runtimeSection = if ($runtime -eq "mono") { "`n[dotnet]`nproject/assembly_name=`"Fixture`"" } else { "" }
            Set-Content -LiteralPath $projectFile -Value "config/features=PackedStringArray(`"4.5.2`")$runtimeSection" -NoNewline

            $tooOld = Run -Cwd $projectPath "local"

            Assert.ExitCode 1 $tooOld "older installed patch must not satisfy the project requirement"
            Assert.False (File.Exists $versionPath)

            Set-Content -LiteralPath $projectFile -Value "config/features=PackedStringArray(`"4.5.0`")$runtimeSection" -NoNewline
            $compatible = Run -Cwd $projectPath "local"

            Assert.ExitCode 0 $compatible "newer compatible patch satisfies the project requirement"
            Assert.Equal $newer.Name (File.Read $versionPath).Trim()
        }
    }

    foreach ($pin in @("", "4.5", "4.5-stable", "4.7-dev-mono")) {
        Test "invalid pin '$pin' never falls back to project detection or the default" {
            $installed = Add-FixtureInstallation "4.5-stable" -Default
            $projectPath = Join-Path $Context.WorkPath "invalid-pin"
            New-Item -ItemType Directory -Path $projectPath -Force | Out-Null
            $versionPath = Join-Path $projectPath ".fgvm-version"
            Set-Content -LiteralPath $versionPath -Value $pin -NoNewline
            Set-Content -LiteralPath (Join-Path $projectPath "project.godot") -Value 'config/features=PackedStringArray("4.5")' -NoNewline

            foreach ($arguments in @(@("local"), @("which"), @("godot", "--attached", "--args", "--version"))) {
                $result = Run -Cwd $projectPath -Arguments $arguments

                Assert.ExitCode $(if ($arguments[0] -eq "local") { 78 } else { 1 }) $result "invalid project pin"
                Assert.Contains ".fgvm-version" $result.Stderr
                Assert.Contains $(if ($pin -eq "4.5-stable") { "requires a runtime" } else { "triplet" }) $result.Stderr
                Assert.Equal "" $result.Stdout.Trim()
                Assert.True ([System.IO.File]::ReadAllText($versionPath) -ceq $pin) "Invalid pins must not be rewritten"
                Assert.False (File.Exists $installed.MockInvocationPath)
            }

            $repair = Run -Cwd $projectPath "local" $installed.Name
            Assert.ExitCode 0 $repair "explicit local query repairs an invalid pin"
            Assert.Equal $installed.Name (File.Read $versionPath).Trim()
        }
    }
}
