using System.Runtime.InteropServices;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Types;
using RuntimeEnvironment = Fgvm.Godot.RuntimeEnvironment;

namespace Fgvm.Tests.Environment;

public class PlatformStringProviderTests
{
    [Theory]
    [InlineData(RuntimeEnvironment.Standard, 1, Architecture.X86, "osx.32")]
    [InlineData(RuntimeEnvironment.Standard, 2, Architecture.X86, "osx32", 0, 4)]
    [InlineData(RuntimeEnvironment.Standard, 2, Architecture.X64, "osx.fat", 0, 4)]
    [InlineData(RuntimeEnvironment.Standard, 3, Architecture.X64, "osx.64", 2)]
    [InlineData(RuntimeEnvironment.Standard, 3, Architecture.Arm64, "osx.universal", 3)]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.X64, "mono_osx64", 0)]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.X64, "mono_osx.64", 2)]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.Arm64, "mono_osx.universal", 5)]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.Arm, null)] // Should return Failure
    [InlineData(RuntimeEnvironment.Mono, 4, Architecture.Arm64, "mono_macos.universal")]
    [InlineData(RuntimeEnvironment.Standard, 4, Architecture.Arm64, "macos.universal")]
    public void MacOS_ShouldReturnCorrectPlatformString(RuntimeEnvironment runtimeEnvironment,
        int major,
        Architecture arch,
        string? expected,
        int minor = 0,
        int? patch = null
    )
    {
        var platformStringProvider = new PlatformStringProvider(new SystemInfo(OS.MacOS, arch));
        var release = new Release(major, minor, patch: patch, runtimeEnvironment: runtimeEnvironment);

        if (expected is null)
        {
            var result = platformStringProvider.GetPlatformString(release);
            var failure = Assert.IsType<Result<string, PlatformError>.Failure>(result);
            var unsupported = Assert.IsType<PlatformError.Unsupported>(failure.Error);
            Assert.Equal(OS.MacOS, unsupported.OS);
            Assert.Equal(arch, unsupported.Architecture);
            return;
        }

        var successResult = platformStringProvider.GetPlatformString(release);
        var success = Assert.IsType<Result<string, PlatformError>.Success>(successResult);
        Assert.Equal(expected, success.Value);
    }

    [Theory]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.X64, "mono_x11_64")]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.X86, "mono_x11_32")]
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.Arm, null)] // Should return Failure
    [InlineData(RuntimeEnvironment.Mono, 3, Architecture.Arm64, null)] // Should return Failure
    [InlineData(RuntimeEnvironment.Standard, 3, Architecture.X64, "x11.64")]
    [InlineData(RuntimeEnvironment.Standard, 3, Architecture.Arm, "linux.arm32")]
    [InlineData(RuntimeEnvironment.Standard, 3, Architecture.Arm64, "linux.arm64")]
    [InlineData(RuntimeEnvironment.Mono, 4, Architecture.X64, "mono_linux_x86_64")]
    [InlineData(RuntimeEnvironment.Mono, 4, Architecture.Arm, "mono_linux_arm32")]
    [InlineData(RuntimeEnvironment.Mono, 4, Architecture.Arm64, "mono_linux_arm64")]
    [InlineData(RuntimeEnvironment.Standard, 4, Architecture.Arm64, "linux.arm64")]
    public void Linux_ShouldReturnCorrectPlatformString(RuntimeEnvironment runtime,
        int major,
        Architecture arch,
        string? expected
    )
    {
        var platformStringProvider = new PlatformStringProvider(new SystemInfo(OS.Linux, arch));
        var release = new Release(major, 0, runtimeEnvironment: runtime);

        if (expected is null)
        {
            var result = platformStringProvider.GetPlatformString(release);
            var failure = Assert.IsType<Result<string, PlatformError>.Failure>(result);
            var unsupported = Assert.IsType<PlatformError.Unsupported>(failure.Error);
            Assert.Equal(OS.Linux, unsupported.OS);
            Assert.Equal(arch, unsupported.Architecture);
            return;
        }

        var successResult = platformStringProvider.GetPlatformString(release);
        var success = Assert.IsType<Result<string, PlatformError>.Success>(successResult);
        Assert.Equal(expected, success.Value);
    }

    [Theory]
    [InlineData(RuntimeEnvironment.Mono, Architecture.X64, "mono_win64")]
    [InlineData(RuntimeEnvironment.Mono, Architecture.X86, "mono_win32")]
    [InlineData(RuntimeEnvironment.Standard, Architecture.X64, "win64.exe")]
    [InlineData(RuntimeEnvironment.Standard, Architecture.X86, "win32.exe")]
    [InlineData(RuntimeEnvironment.Mono, Architecture.Arm64, "mono_windows_arm64", 4, 3)]
    [InlineData(RuntimeEnvironment.Standard, Architecture.Arm64, "windows_arm64.exe", 4, 3)]
    [InlineData(RuntimeEnvironment.Mono, Architecture.Arm64, null, 3, 0)]
    [InlineData(RuntimeEnvironment.Standard, Architecture.Arm64, null, 3, 0)]
    [InlineData(RuntimeEnvironment.Mono, Architecture.Arm64, null, 4, 2)]
    [InlineData(RuntimeEnvironment.Standard, Architecture.Arm64, null, 4, 2)]
    public void Windows_ShouldReturnCorrectPlatformString(RuntimeEnvironment runtime,
        Architecture arch,
        string? expected,
        int major = 3,
        int minor = 0
    )
    {
        var platformStringProvider = new PlatformStringProvider(new SystemInfo(OS.Windows, arch));
        var release = new Release(major, minor, runtimeEnvironment: runtime);

        if (expected is null)
        {
            var result = platformStringProvider.GetPlatformString(release);
            var failure = Assert.IsType<Result<string, PlatformError>.Failure>(result);
            var unsupported = Assert.IsType<PlatformError.Unsupported>(failure.Error);
            Assert.Equal(OS.Windows, unsupported.OS);
            Assert.Equal(arch, unsupported.Architecture);
            return;
        }

        var successResult = platformStringProvider.GetPlatformString(release);
        var success = Assert.IsType<Result<string, PlatformError>.Success>(successResult);
        Assert.Equal(expected, success.Value);
    }

    [Theory]
    [InlineData(OS.Unknown)]
    [InlineData(OS.FreeBSD)]
    public void UnsupportedOS_ShouldReturnFailure(OS os)
    {
        var platformStringProvider = new PlatformStringProvider(new SystemInfo(os, Architecture.X64));
        var release = new Release(4, 3);
        var result = platformStringProvider.GetPlatformString(release);
        var failure = Assert.IsType<Result<string, PlatformError>.Failure>(result);
        Assert.IsType<PlatformError.Unsupported>(failure.Error);
        var unsupported = (PlatformError.Unsupported)failure.Error;
        Assert.Equal(os, unsupported.OS);
        Assert.Equal(Architecture.X64, unsupported.Architecture);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("mono_macos.universal", "macos.universal")]
    [InlineData("mono_osx.universal", "osx.universal")]
    [InlineData("mono_osx.64", "osx.64")]
    [InlineData("mono_osx64", "osx.64")]
    [InlineData("mono_linux_x86_64", "linux.x86_64")]
    [InlineData("mono_linux_x86_32", "linux.x86_32")]
    [InlineData("mono_linux_arm32", "linux.arm32")]
    [InlineData("mono_linux_arm64", "linux.arm64")]
    [InlineData("mono_x11_64", "x11.64")]
    [InlineData("mono_x11_32", "x11.32")]
    [InlineData("mono_windows_arm64", "windows_arm64")]
    [InlineData("mono_win64", "win64")]
    [InlineData("mono_win32", "win32")]
    [InlineData("windows_arm64.exe", "windows_arm64")]
    [InlineData("win64.exe", "win64")]
    [InlineData("linux_x86_64", "linux.x86_64")]
    [InlineData("macos.universal", "macos.universal")]
    public void GetCatalogTargetId_NormalizesPlatformString(string? platformString, string expected)
    {
        var targetId = PlatformStringProvider.GetCatalogTargetId(platformString);

        Assert.Equal(expected, targetId);
    }
}
