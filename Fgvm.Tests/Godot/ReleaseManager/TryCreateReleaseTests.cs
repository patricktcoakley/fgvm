using System.Runtime.InteropServices;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using RuntimeEnvironment = Fgvm.Godot.RuntimeEnvironment;

namespace Fgvm.Tests.Godot.ReleaseManager;

using static TestData;

public class TryCreateReleaseTests
{
    [Theory]
    [MemberData(nameof(GetExecNameTestData), MemberType = typeof(TestData))]
    public void ExecName_ShouldReturnTheCorrectExecutable(OS os,
        Architecture arch,
        string versionInfo,
        string expectedExecName
    )
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(os, arch)
            .Build();

        var release = releaseManager.TryCreateRelease(versionInfo);

        Assert.NotNull(release);
        Assert.Equal(expectedExecName, release.ExecName);
    }

    [Theory]
    [MemberData(nameof(DetermineFullNameTestData), MemberType = typeof(TestData))]
    public void Release_FileName_ReturnsExpectedFileName(string version,
        RuntimeEnvironment runtimeEnvironment,
        OS os,
        Architecture arch,
        string expected
    )
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(os, arch)
            .Build();

        var release = releaseManager.TryCreateRelease($"{version}-{runtimeEnvironment.Name()}");

        Assert.NotNull(release);
        Assert.Equal(runtimeEnvironment, release.RuntimeEnvironment);
        Assert.Equal(expected, release.FileName);
    }

    [Theory]
    [MemberData(nameof(GetReleasePropertyTestData), MemberType = typeof(TestData))]
    public void Release_Properties_ShouldBeCorrectlySet(OS os,
        Architecture arch,
        string versionString,
        RuntimeEnvironment expectedRuntime,
        string expectedExecName,
        string expectedFileName
    )
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(os, arch)
            .Build();

        var release = releaseManager.TryCreateRelease(versionString);

        Assert.NotNull(release);
        Assert.Equal(expectedRuntime, release.RuntimeEnvironment);
        Assert.Equal(expectedExecName, release.ExecName);
        Assert.Equal(expectedFileName, release.ZipFileName);
    }

    [Theory]
    [InlineData(OS.MacOS, Architecture.X64, "4.3-stable-standard", "macos.universal")]
    [InlineData(OS.MacOS, Architecture.Arm64, "4.3-stable-standard", "macos.universal")]
    [InlineData(OS.MacOS, Architecture.X64, "4.3-stable-mono", "mono_macos.universal")]
    [InlineData(OS.MacOS, Architecture.Arm64, "4.3-stable-mono", "mono_macos.universal")]
    [InlineData(OS.MacOS, Architecture.X64, "3.5-stable-standard", "osx.universal")]
    [InlineData(OS.MacOS, Architecture.Arm64, "3.5-stable-standard", "osx.universal")]
    [InlineData(OS.MacOS, Architecture.X64, "3.5-stable-mono", "mono_osx.universal")]
    [InlineData(OS.MacOS, Architecture.Arm64, "3.5-stable-mono", "mono_osx.universal")]
    [InlineData(OS.Linux, Architecture.X64, "4.3-stable-standard", "linux.x86_64")]
    [InlineData(OS.Linux, Architecture.X86, "4.3-stable-standard", "linux.x86_32")]
    [InlineData(OS.Linux, Architecture.Arm, "4.3-stable-standard", "linux.arm32")]
    [InlineData(OS.Linux, Architecture.Arm64, "4.3-stable-standard", "linux.arm64")]
    [InlineData(OS.Linux, Architecture.X64, "4.3-stable-mono", "mono_linux_x86_64")]
    [InlineData(OS.Linux, Architecture.X86, "4.3-stable-mono", "mono_linux_x86_32")]
    [InlineData(OS.Linux, Architecture.Arm, "4.3-stable-mono", "mono_linux_arm32")]
    [InlineData(OS.Linux, Architecture.Arm64, "4.3-stable-mono", "mono_linux_arm64")]
    [InlineData(OS.Linux, Architecture.X64, "3.5-stable-standard", "x11.64")]
    [InlineData(OS.Linux, Architecture.X86, "3.5-stable-standard", "x11.32")]
    [InlineData(OS.Linux, Architecture.Arm, "3.5-stable-standard", "linux.arm32")]
    [InlineData(OS.Linux, Architecture.Arm64, "3.5-stable-standard", "linux.arm64")]
    [InlineData(OS.Linux, Architecture.X64, "3.5-stable-mono", "mono_x11_64")]
    [InlineData(OS.Linux, Architecture.X86, "3.5-stable-mono", "mono_x11_32")]
    [InlineData(OS.Windows, Architecture.X64, "4.3-stable-standard", "win64.exe")]
    [InlineData(OS.Windows, Architecture.X86, "4.3-stable-standard", "win32.exe")]
    [InlineData(OS.Windows, Architecture.Arm64, "4.3-stable-standard", "windows_arm64.exe")]
    [InlineData(OS.Windows, Architecture.X64, "4.3-stable-mono", "mono_win64")]
    [InlineData(OS.Windows, Architecture.X86, "4.3-stable-mono", "mono_win32")]
    [InlineData(OS.Windows, Architecture.Arm64, "4.3-stable-mono", "mono_windows_arm64")]
    [InlineData(OS.Windows, Architecture.X64, "3.5-stable-standard", "win64.exe")]
    [InlineData(OS.Windows, Architecture.X86, "3.5-stable-standard", "win32.exe")]
    [InlineData(OS.Windows, Architecture.X64, "3.5-stable-mono", "mono_win64")]
    [InlineData(OS.Windows, Architecture.X86, "3.5-stable-mono", "mono_win32")]
    public void TryCreateRelease_SupportedPlatform_ReturnsRelease(OS os,
        Architecture arch,
        string versionString,
        string expectedPlatformString
    )
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(os, arch)
            .Build();

        var release = releaseManager.TryCreateRelease(versionString);

        Assert.NotNull(release);
        Assert.Equal(expectedPlatformString, release.PlatformString);
        Assert.Equal(os, release.OS);
    }

    [Theory]
    [InlineData(OS.MacOS, Architecture.X86, "3.5-stable-mono")] // macOS X86 mono v3 not supported
    [InlineData(OS.MacOS, Architecture.Arm, "4.3-stable-standard")] // macOS 32-bit ARM not supported
    [InlineData(OS.MacOS, Architecture.Arm, "3.5-stable-mono")] // macOS ARM mono v3 not supported
    [InlineData(OS.Linux, Architecture.Arm, "3.5-stable-mono")] // Linux ARM mono v3 not supported
    [InlineData(OS.Linux, Architecture.Arm64, "3.5-stable-mono")] // Linux ARM64 mono v3 not supported
    [InlineData(OS.Windows, Architecture.Arm64, "4.2-stable-standard")] // Windows ARM64 standard before 4.3 not supported
    [InlineData(OS.Windows, Architecture.Arm64, "3.5-stable-standard")] // Windows ARM64 v3 not supported
    [InlineData(OS.Windows, Architecture.Arm64, "3.5-stable-mono")] // Windows ARM64 mono v3 not supported
    [InlineData(OS.Windows, Architecture.Arm, "4.3-stable-standard")] // Windows 32-bit ARM not supported
    [InlineData(OS.FreeBSD, Architecture.X64, "4.3-stable-standard")] // FreeBSD not supported
    [InlineData(OS.Unknown, Architecture.X64, "4.3-stable-standard")] // Unknown OS not supported
    public void TryCreateRelease_UnsupportedPlatform_ReturnsNull(OS os, Architecture arch, string versionString)
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(os, arch)
            .Build();

        var release = releaseManager.TryCreateRelease(versionString);

        Assert.Null(release);
    }

    [Fact]
    public void CreateReleaseWithoutPlatform_DoesNotBindToUnsupportedHostTarget()
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(OS.Windows, Architecture.Arm64)
            .Build();

        var result = releaseManager.CreateReleaseWithoutPlatform("4.2-stable-standard");

        var success = Assert.IsType<Result<Release, ReleaseParseError>.Success>(result);
        Assert.Equal("4.2-stable-standard", success.Value.ReleaseNameWithRuntime);
        Assert.Null(success.Value.PlatformString);
        Assert.Equal(default, success.Value.OS);
    }

    [Theory]
    [InlineData("invalid-version")]
    [InlineData("not.a.version")]
    [InlineData("")]
    [InlineData("xyz")]
    public void TryCreateRelease_InvalidVersionString_ReturnsNull(string invalidVersionString)
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(OS.Windows, Architecture.X64)
            .Build();

        var release = releaseManager.TryCreateRelease(invalidVersionString);

        Assert.Null(release);
    }
}
