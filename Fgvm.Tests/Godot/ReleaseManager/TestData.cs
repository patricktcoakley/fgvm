using System.Runtime.InteropServices;
using Fgvm.Environment;
using Godot_RuntimeEnvironment = Fgvm.Godot.RuntimeEnvironment;

namespace Fgvm.Tests.Godot.ReleaseManager;

public static class TestData
{
    public static readonly IEnumerable<string> TestReleases =
    [
        "4.4-dev5",
        "4.2-stable",
        "4.1-rc2",
        "4.0-beta3",
        "3.5-stable",
        "3.2-alpha4",
        "3.1.2-rc1",
        "2.0.1-stable",
        "2.0-stable",
        "1.1-stable"
    ];

    public static TheoryData<string[], string[]> SearchReleaseTestCases()
    {
        var data = new TheoryData<string[], string[]>();

        data.Add([], TestReleases.ToArray());
        data.Add(["5"], []);
        data.Add(["4"], ["4.4-dev5", "4.2-stable", "4.1-rc2", "4.0-beta3"]);
        data.Add(["4", "rc"], ["4.1-rc2"]);
        data.Add(["rc", "4.1"], ["4.1-rc2"]);
        data.Add(["stable"], ["4.2-stable", "3.5-stable", "2.0.1-stable", "2.0-stable", "1.1-stable"]);
        data.Add(["rc"], ["4.1-rc2", "3.1.2-rc1"]);

        return data;
    }


    public static TheoryData<OS, Architecture, string, string> GetExecNameTestData()
    {
        var data = new TheoryData<OS, Architecture, string, string>
        {
            // Windows v4
            { OS.Windows, Architecture.X64, "4.4-dev4-standard", "Godot_v4.4-dev4_win64.exe" },
            { OS.Windows, Architecture.X86, "4.4-dev4-standard", "Godot_v4.4-dev4_win32.exe" },
            { OS.Windows, Architecture.Arm64, "4.4-dev4-standard", "Godot_v4.4-dev4_windows_arm64.exe" },
            { OS.Windows, Architecture.X64, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_win64.exe" },
            { OS.Windows, Architecture.X86, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_win32.exe" },
            { OS.Windows, Architecture.Arm64, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_windows_arm64.exe" },

            // Linux v4
            { OS.Linux, Architecture.X64, "4.4-dev4-standard", "Godot_v4.4-dev4_linux.x86_64" },
            { OS.Linux, Architecture.X86, "4.4-dev4-standard", "Godot_v4.4-dev4_linux.x86_32" },
            { OS.Linux, Architecture.Arm, "4.4-dev4-standard", "Godot_v4.4-dev4_linux.arm32" },
            { OS.Linux, Architecture.Arm64, "4.4-dev4-standard", "Godot_v4.4-dev4_linux.arm64" },
            { OS.Linux, Architecture.X64, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_linux.x86_64" },
            { OS.Linux, Architecture.X86, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_linux.x86_32" },
            { OS.Linux, Architecture.Arm, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_linux.arm32" },
            { OS.Linux, Architecture.Arm64, "4.4-dev4-mono", "Godot_v4.4-dev4_mono_linux.arm64" },

            // MacOS v4
            { OS.MacOS, Architecture.X64, "4.4-dev4-standard", "Godot.app" },
            { OS.MacOS, Architecture.Arm64, "4.4-dev4-standard", "Godot.app" },
            { OS.MacOS, Architecture.X64, "4.4-dev4-mono", "Godot_mono.app" },
            { OS.MacOS, Architecture.Arm64, "4.4-dev4-mono", "Godot_mono.app" },

            // Linux v3
            { OS.Linux, Architecture.X64, "3.5-stable-standard", "Godot_v3.5-stable_x11.64" },
            { OS.Linux, Architecture.X86, "3.5-stable-standard", "Godot_v3.5-stable_x11.32" },
            { OS.Linux, Architecture.X64, "3.5-stable-mono", "Godot_v3.5-stable_mono_x11_64" },
            { OS.Linux, Architecture.X86, "3.5-stable-mono", "Godot_v3.5-stable_mono_x11_32" },

            // MacOS v3
            { OS.MacOS, Architecture.X64, "3.5-stable-standard", "Godot.app" },
            { OS.MacOS, Architecture.X64, "3.5-stable-mono", "Godot_mono.app" },

            // Misc
            { OS.Windows, Architecture.X64, "4.2-rc1-standard", "Godot_v4.2-rc1_win64.exe" },
            { OS.Windows, Architecture.X64, "4.2-beta2-mono", "Godot_v4.2-beta2_mono_win64.exe" },
            { OS.Linux, Architecture.X64, "4.2-alpha3-standard", "Godot_v4.2-alpha3_linux.x86_64" },
            { OS.Linux, Architecture.X64, "4.2-stable-mono", "Godot_v4.2-stable_mono_linux.x86_64" }
        };

        return data;
    }

    public static TheoryData<OS, Architecture, string, Godot_RuntimeEnvironment, string, string> GetReleasePropertyTestData()
    {
        var data = new TheoryData<OS, Architecture, string, Godot_RuntimeEnvironment, string, string>
        {
            {
                OS.Windows, Architecture.X64, "4.2-stable-mono", Godot_RuntimeEnvironment.Mono, "Godot_v4.2-stable_mono_win64.exe",
                "Godot_v4.2-stable_mono_win64.zip"
            },
            {
                OS.Windows, Architecture.X64, "4.2-rc2-standard", Godot_RuntimeEnvironment.Standard, "Godot_v4.2-rc2_win64.exe",
                "Godot_v4.2-rc2_win64.exe.zip"
            },
            {
                OS.Windows, Architecture.X86, "4.2-stable-mono", Godot_RuntimeEnvironment.Mono, "Godot_v4.2-stable_mono_win32.exe",
                "Godot_v4.2-stable_mono_win32.zip"
            },
            {
                OS.Linux, Architecture.X64, "4.2-stable-mono", Godot_RuntimeEnvironment.Mono, "Godot_v4.2-stable_mono_linux.x86_64",
                "Godot_v4.2-stable_mono_linux_x86_64.zip"
            },
            {
                OS.Linux, Architecture.X86, "4.2-beta5-standard", Godot_RuntimeEnvironment.Standard, "Godot_v4.2-beta5_linux.x86_32",
                "Godot_v4.2-beta5_linux.x86_32.zip"
            },
            {
                OS.MacOS, Architecture.X64, "4.2-stable-mono", Godot_RuntimeEnvironment.Mono, "Godot_mono.app",
                "Godot_v4.2-stable_mono_macos.universal.zip"
            },
            {
                OS.MacOS, Architecture.Arm64, "4.2-rc1-standard", Godot_RuntimeEnvironment.Standard, "Godot.app",
                "Godot_v4.2-rc1_macos.universal.zip"
            }
        };

        return data;
    }

    public static IEnumerable<object[]> DetermineFullNameTestData()
    {
        // Cursed matrix testing
        foreach (var releaseType in new[] { "rc1", "dev2", "beta3", "alpha4", "stable" })
        {
            // Version 3 Mono
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.MacOS, Architecture.Arm64,
                $"Godot_v3.6-{releaseType}_mono_osx.universal"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Windows, Architecture.X86, $"Godot_v3.6-{releaseType}_mono_win32"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Windows, Architecture.X64, $"Godot_v3.6-{releaseType}_mono_win64"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Linux, Architecture.X86, $"Godot_v3.6-{releaseType}_mono_x11_32"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Linux, Architecture.X64, $"Godot_v3.6-{releaseType}_mono_x11_64"
            ];

            // Version 3 Standard
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.MacOS, Architecture.X64,
                $"Godot_v3.6-{releaseType}_osx.universal"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Windows, Architecture.X64, $"Godot_v3.6-{releaseType}_win64.exe"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Windows, Architecture.X86, $"Godot_v3.6-{releaseType}_win32.exe"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.Arm, $"Godot_v3.6-{releaseType}_linux.arm32"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.Arm64,
                $"Godot_v3.6-{releaseType}_linux.arm64"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.X64, $"Godot_v3.6-{releaseType}_x11.64"
            ];
            yield return
            [
                $"3.6-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.X86, $"Godot_v3.6-{releaseType}_x11.32"
            ];

            // Version 4 Mono
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.MacOS, Architecture.Arm64,
                $"Godot_v4.3-{releaseType}_mono_macos.universal"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Windows, Architecture.X86, $"Godot_v4.3-{releaseType}_mono_win32"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Windows, Architecture.X64, $"Godot_v4.3-{releaseType}_mono_win64"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Windows, Architecture.Arm64,
                $"Godot_v4.3-{releaseType}_mono_windows_arm64"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Linux, Architecture.X86,
                $"Godot_v4.3-{releaseType}_mono_linux_x86_32"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Linux, Architecture.X64,
                $"Godot_v4.3-{releaseType}_mono_linux_x86_64"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Linux, Architecture.Arm,
                $"Godot_v4.3-{releaseType}_mono_linux_arm32"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Mono, OS.Linux, Architecture.Arm64,
                $"Godot_v4.3-{releaseType}_mono_linux_arm64"
            ];

            // Version 4 Standard
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.MacOS, Architecture.X64,
                $"Godot_v4.3-{releaseType}_macos.universal"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Windows, Architecture.X64, $"Godot_v4.3-{releaseType}_win64.exe"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Windows, Architecture.X86, $"Godot_v4.3-{releaseType}_win32.exe"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Windows, Architecture.Arm64,
                $"Godot_v4.3-{releaseType}_windows_arm64.exe"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.Arm, $"Godot_v4.3-{releaseType}_linux.arm32"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.Arm64,
                $"Godot_v4.3-{releaseType}_linux.arm64"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.X64,
                $"Godot_v4.3-{releaseType}_linux.x86_64"
            ];
            yield return
            [
                $"4.3-{releaseType}", Godot_RuntimeEnvironment.Standard, OS.Linux, Architecture.X86,
                $"Godot_v4.3-{releaseType}_linux.x86_32"
            ];
        }
    }
}
