using System.Runtime.InteropServices;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Tests.Godot.ReleaseManager;
using Fgvm.Types;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.Extensions.Logging;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Services;

public sealed class InstalledVersionResolutionPropertyTests
{
    private static Gen<string> VersionGen =>
        Gen.OneOf(
            Gen.Choose(3, 5).Zip(Gen.Choose(0, 9)).Select(t => $"{t.Item1}.{t.Item2}"),
            Gen.Choose(3, 5).Zip(Gen.Choose(0, 9)).Zip(Gen.Choose(0, 3))
                .Select(t => $"{t.Item1.Item1}.{t.Item1.Item2}.{t.Item2}")
        );

    private static Gen<string> InstalledVersionGen =>
        VersionGen.Zip(ReleaseTypeGen).Zip(RuntimeGen)
            .Select(t => $"{t.Item1.Item1}-{t.Item1.Item2}-{t.Item2}");

    private static Gen<string> ReleaseTypeGen =>
        Gen.OneOf(
            Gen.Constant("stable"),
            Gen.Choose(1, 5).Select(value => $"rc{value}"),
            Gen.Choose(1, 5).Select(value => $"beta{value}"),
            Gen.Choose(1, 5).Select(value => $"alpha{value}"),
            Gen.Choose(1, 5).Select(value => $"dev{value}")
        );

    private static Gen<string> RuntimeGen =>
        Gen.Elements("standard", "mono");

    private static Gen<string[]> InstalledVersionsGen =>
        InstalledVersionGen.ListOf()
            .Select(list => list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            .Where(list => list.Length > 0);

    [Property]
    public FsCheck.Property ExactInstalledVersionsResolveToThemselves() =>
        Prop.ForAll(InstalledVersionsGen.ToArbitrary(), installedVersions =>
        {
            var service = CreateService(installedVersions);

            return installedVersions.All(installedVersion =>
            {
                var result = service.ResolveInstalledVersionAsync([installedVersion])
                    .GetAwaiter()
                    .GetResult();

                return result is Result<VersionResolutionOutcome.Found, VersionResolutionError>.Success(var found) &&
                       found.VersionName == installedVersion;
            });
        });

    [Property]
    public FsCheck.Property RuntimeSpecificQueryDoesNotResolveUninstalledRuntime() =>
        Prop.ForAll(VersionGen.ToArbitrary(), RuntimeGen.ToArbitrary(), (version, installedRuntime) =>
        {
            var missingRuntime = installedRuntime == "mono" ? "standard" : "mono";
            var installedVersion = $"{version}-stable-{installedRuntime}";
            var service = CreateService([installedVersion]);

            var result = service.ResolveInstalledVersionAsync([version, missingRuntime])
                .GetAwaiter()
                .GetResult();

            return result is Result<VersionResolutionOutcome.Found, VersionResolutionError>.Failure
            {
                Error: VersionResolutionError.NotFound
            };
        });

    private static VersionManagementService CreateService(IReadOnlyList<string> installedVersions)
    {
        var releaseManager = new ReleaseManagerBuilder()
            .WithOSAndArch(OS.Windows, Architecture.X64)
            .Build();

        var registry = new Mock<IInstallationRegistry>();
        var installations = installedVersions.Select(CreateInstallation).ToArray();
        registry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(installations));

        foreach (var installation in installations)
        {
            registry.Setup(x => x.FindByReleaseName(installation.ReleaseNameWithRuntime))
                .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));
        }

        var pathService = new Mock<IPathService>();
        pathService.Setup(x => x.RootPath).Returns("/test/fgvm");

        var projectManager = new Mock<IProjectManager>();
        projectManager.Setup(x => x.FindExplicitProjectInfo(It.IsAny<string>()))
            .Returns(new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing()));
        projectManager.Setup(x => x.FindProjectInfo(It.IsAny<string>()))
            .Returns(new Result<ProjectLookup<Release>, ProjectError>.Success(new ProjectLookup<Release>.Missing()));

        var hostSystem = new Mock<IHostSystem>();
        hostSystem.Setup(x => x.SystemInfo)
            .Returns(new SystemInfo(OS.Windows, Architecture.X64));

        return new VersionManagementService(
            hostSystem.Object,
            releaseManager,
            registry.Object,
            new Mock<IInstallationService>().Object,
            new Mock<IInstallationOrchestrator>().Object,
            pathService.Object,
            projectManager.Object,
            new TestConsole(),
            new Mock<ILogger<VersionManagementService>>().Object);
    }

    private static Installation CreateInstallation(string releaseNameWithRuntime)
    {
        var target = releaseNameWithRuntime.EndsWith("-mono", StringComparison.OrdinalIgnoreCase)
            ? "mono_win64"
            : "win64.exe";
        return new Installation(
            $"{releaseNameWithRuntime}@{target}",
            releaseNameWithRuntime,
            target,
            $"installations/{releaseNameWithRuntime}/{target}",
            null,
            null);
    }
}
