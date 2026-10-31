Set-StrictMode -Version Latest

if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) {
    # As of 4.8 beta 1 macOS binaries are distributed per architecture
    Suite "macOS distribution" {
        $architecture = if ([System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64) { "arm64" } else { "x86_64" }

        foreach ($runtime in @("standard", "mono")) {
            Test "installs 4.8 dev7 universal and beta1 architecture-specific binaries for $runtime" {
                $Context.FixtureManifestPath = Join-Path (Split-Path -Parent $Context.FixtureManifestPath) "comparison-manifest.json"

                $dev = Run "install" "4.8-dev7-$runtime"
                $beta = Run "install" "4.8-beta1-$runtime"

                Assert.ExitCode 0 $dev "install 4.8 dev7 $runtime binary"
                Assert.ExitCode 0 $beta "install 4.8 beta1 $runtime binary"

                $prefix = if ($runtime -eq "mono") { "mono_" } else { "" }
                $registry = Manifest.From $Context.InstallationsPath
                Assert.Contains "4.8-dev7-$runtime@$($prefix)macos.universal" $registry["installations"].Keys
                Assert.Contains "4.8-beta1-$runtime@$($prefix)macos.$architecture" $registry["installations"].Keys
            }
        }
    }
}
