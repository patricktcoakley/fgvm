using System.Text.Json;
using System.Xml.Linq;
using Fgvm.Error;

namespace Fgvm.Tests.EndToEnd;

/// <summary>
///     Collection definition to ensure end-to-end tests run sequentially.
///     Tests share a single runner and modify shared Fgvm state.
/// </summary>
[CollectionDefinition("EndToEnd", DisableParallelization = true)]
public class EndToEndCollection;

[Collection("EndToEnd")]
public class EndToEndTests(TestFixture fixture) : IClassFixture<TestFixture>
{
    private const string StableRelease = "4.6.2-stable";
    private const string StableVersionQuery = "4.6";
    private const string StableMonoRelease = "4.6.2-stable-mono";
    private const string RcRelease = "4.6.2-rc2";
    private const string OlderRelease = "4.5-stable";
    private const string GodotMockVersion = "4.6.2.stable.standard.mock";

    [Fact]
    public async Task DisplaysHelpWhenNoArgumentsProvided()
    {
        var result = await fixture.ExecuteCommand([]);

        await fixture.AssertSuccessfulExecutionAsync(result);
        Assert.Contains("Usage:", result.Stdout);
        Assert.Contains("Commands:", result.Stdout);
    }

    [Fact]
    public async Task DisplaysVersionWithVersionFlag()
    {
        var expected = GetProjectVersion();
        var result = await fixture.ExecuteCommand(["--version"]);

        await fixture.AssertSuccessfulExecutionAsync(result, expected);
    }

    [Fact]
    public async Task CreatesIsolatedFgvmDirectoryOnFirstCommand()
    {
        await fixture.ExecuteCommand(["list"]);

        Assert.True(await fixture.DirectoryExists(fixture.RootPath));
    }

    [Fact]
    public async Task UsesFgvmHomeEnvironmentVariableForRootPath()
    {
        var customHome = NewTempPath("fgvm-env-test");
        var fgvmRoot = Path.Combine(customHome, "fgvm");

        try
        {
            var result = await fixture.ExecuteCommandWithEnvironment(["list"], new Dictionary<string, string>
            {
                ["FGVM_HOME"] = customHome
            });

            await fixture.AssertSuccessfulExecutionAsync(result);
            Assert.True(await fixture.DirectoryExists(fgvmRoot), $"Expected Fgvm root to be created at '{fgvmRoot}'.");
        }
        finally
        {
            await fixture.DeletePath(customHome);
        }
    }

    [Fact]
    public async Task SearchCommandUsesFixtureCatalogAndSupportsJsonOutput()
    {
        var result = await fixture.ExecuteCommand(["search", "--json", StableVersionQuery]);

        await fixture.AssertSuccessfulExecutionAsync(result);

        using var document = JsonDocument.Parse(result.Stdout.Trim());
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Contains(document.RootElement.EnumerateArray(),
            item => item.ToString().Contains(StableRelease, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchCommandCreatesReleasesJsonWhenMissing()
    {
        var home = NewTempPath("fgvm-cache-missing");
        var releasesPath = Path.Combine(home, "fgvm", "releases.json");

        try
        {
            var result = await fixture.ExecuteCommandWithEnvironment(["search", "--json", StableVersionQuery],
                new Dictionary<string, string>
                {
                    ["FGVM_HOME"] = home
                });

            await fixture.AssertSuccessfulExecutionAsync(result, "search");
            Assert.True(await fixture.FileExists(releasesPath), "Expected search to create releases.json when no cache exists.");

            var content = await fixture.ReadFile(releasesPath);
            using var document = JsonDocument.Parse(content);
            Assert.True(document.RootElement.TryGetProperty("lastUpdated", out _));
            Assert.True(document.RootElement.TryGetProperty("releases", out var releases));
            Assert.True(releases.TryGetProperty("4.6.2", out var release));
            Assert.True(release.TryGetProperty("stable", out _));
        }
        finally
        {
            await fixture.DeletePath(home);
        }
    }

    [Fact]
    public async Task SearchNoCacheRefreshesSeededReleasesJsonFromFixtures()
    {
        var home = NewTempPath("fgvm-no-cache-refresh");
        var root = Path.Combine(home, "fgvm");
        var releasesPath = Path.Combine(root, "releases.json");

        try
        {
            await fixture.CreateDirectory(root);
            await fixture.WriteFile(releasesPath,
                "{\"lastUpdated\":\"2999-01-01T00:00:00+00:00\",\"releases\":{\"4.999\":{\"stable\":{}}}}");

            var cached = await fixture.ExecuteCommandWithEnvironment(["search", "--json", "4.999"], new Dictionary<string, string>
            {
                ["FGVM_HOME"] = home
            });
            await fixture.AssertSuccessfulExecutionAsync(cached, "cached search");
            Assert.Contains("4.999-stable", cached.Stdout);

            var refreshed = await fixture.ExecuteCommandWithEnvironment(["search", "--no-cache", "--json", "4.999"],
                new Dictionary<string, string>
                {
                    ["FGVM_HOME"] = home
                });
            await fixture.AssertSuccessfulExecutionAsync(refreshed, "search --no-cache");
            Assert.DoesNotContain("4.999-stable", refreshed.Stdout);
            Assert.DoesNotContain("4.999", await fixture.ReadFile(releasesPath));
        }
        finally
        {
            await fixture.DeletePath(home);
        }
    }

    [Fact]
    public async Task InstallSetLaunchAndPathShimUseMockGodot()
    {
        var install = await fixture.ExecuteCommand(["install", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var set = await fixture.ExecuteCommand(["set", StableVersionQuery]);
        await fixture.AssertSuccessfulExecutionAsync(set, "set");

        var which = await fixture.ExecuteCommand(["which"]);
        await fixture.AssertSuccessfulExecutionAsync(which, "which");
        Assert.Contains(StableVersionQuery, which.Stdout);

        var godotVersion = await fixture.ExecuteCommand(["godot", "--args", "--version --headless"]);
        await fixture.AssertSuccessfulExecutionAsync(godotVersion, "godot --version");
        Assert.Contains(GodotMockVersion, godotVersion.Stdout);

        var pathShimVersion = await fixture.ExecuteGodotShim(["--version", "--headless"]);
        await fixture.AssertSuccessfulExecutionAsync(pathShimVersion, "path shim godot --version");
        Assert.Contains(GodotMockVersion, pathShimVersion.Stdout);

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task InstallingMonoRuntimeWorks()
    {
        var install = await fixture.ExecuteCommand(["install", StableVersionQuery, "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install mono");

        Assert.True(await fixture.HasVersionInstalled("mono"), "Mono runtime version not found in installed versions.");

        var set = await fixture.ExecuteCommand(["set", StableVersionQuery, "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(set, "set mono");

        var godotVersion = await fixture.ExecuteCommand(["godot", "--args", "--version --headless"]);
        await fixture.AssertSuccessfulExecutionAsync(godotVersion, "mono godot --version");
        Assert.Contains(GodotMockVersion, godotVersion.Stdout);

        await CleanupVersion(StableMonoRelease);
    }

    [Fact]
    public async Task MockGodotFailureExitCodesPropagateThroughAttachedMode()
    {
        await fixture.EnsureVersionInstalled(StableRelease);
        await fixture.ExecuteCommand(["set", StableRelease]);

        var invalid = await fixture.ExecuteCommand(["godot", "--attached", "--args", "--fgvm-mock-invalid-arg"]);
        Assert.Equal(ExitCodes.ArgumentError, invalid.ExitCode);
        Assert.Contains("invalid argument", invalid.Stdout, StringComparison.OrdinalIgnoreCase);

        var failure = await fixture.ExecuteCommand(["godot", "--attached", "--args", "--fgvm-mock-fail"]);
        Assert.Equal(42, failure.ExitCode);
        Assert.Contains("failure", failure.Stdout, StringComparison.OrdinalIgnoreCase);

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task InstallWithMajorQueryPrefersStableOverDevFixture()
    {
        var install = await fixture.ExecuteCommand(["install", "4"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install 4");

        var list = await fixture.ExecuteCommand(["list"]);
        await fixture.AssertSuccessfulExecutionAsync(list, "list");

        Assert.Contains(StableRelease, list.Stdout);
        Assert.DoesNotContain("-dev", list.Stdout);

        await CleanupVersion("4");
    }

    [Fact]
    public async Task MultipleSequentialInstallsAreIdempotent()
    {
        var install1 = await fixture.ExecuteCommand(["install", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(install1, "first install");

        var install2 = await fixture.ExecuteCommand(["install", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(install2, "second install");

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task FullVersionManagementWorkflow()
    {
        var installStable = await fixture.ExecuteCommand(["install", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(installStable, "install stable");

        var installRc = await fixture.ExecuteCommand(["install", RcRelease]);
        await fixture.AssertSuccessfulExecutionAsync(installRc, "install rc");

        var setStable = await fixture.ExecuteCommand(["set", StableVersionQuery]);
        await fixture.AssertSuccessfulExecutionAsync(setStable, "set stable");
        Assert.Contains(StableVersionQuery, await fixture.GetCurrentVersion());

        var setRc = await fixture.ExecuteCommand(["set", "rc2"]);
        await fixture.AssertSuccessfulExecutionAsync(setRc, "set rc");
        Assert.Contains("rc2", await fixture.GetCurrentVersion());

        var projectPath = NewTempPath("workflow-project");
        await fixture.CreateDirectory(projectPath);
        var local = await fixture.ExecuteCommandInDirectory(projectPath, ["local", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(local, "local stable");

        Assert.True(await fixture.FileExists(Path.Combine(projectPath, ".fgvm-version")));

        await CleanupVersion(RcRelease);
        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task FirstSelectedVersionRemainsDefaultWhenInstallingSecondVersion()
    {
        await fixture.DeletePath(Path.Combine(fixture.RootPath, ".version"));

        var installOlder = await fixture.ExecuteCommand(["install", OlderRelease]);
        await fixture.AssertSuccessfulExecutionAsync(installOlder, "install older");

        var setOlder = await fixture.ExecuteCommand(["set", "4.5"]);
        await fixture.AssertSuccessfulExecutionAsync(setOlder, "set older");

        var installStable = await fixture.ExecuteCommand(["install", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(installStable, "install stable");

        var which = await fixture.ExecuteCommand(["which"]);
        await fixture.AssertSuccessfulExecutionAsync(which, "which");
        Assert.Contains("4.5", which.Stdout);

        await CleanupVersion(OlderRelease);
        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task LocalCommandCreatesVersionFileInCurrentDirectory()
    {
        await fixture.EnsureVersionInstalled(StableRelease);

        var projectPath = NewTempPath("local-content-test");
        await fixture.CreateDirectory(projectPath);

        var result = await fixture.ExecuteCommandInDirectory(projectPath, ["local", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(result, "local");

        var versionFile = Path.Combine(projectPath, ".fgvm-version");
        Assert.True(await fixture.FileExists(versionFile));
        Assert.Contains(StableVersionQuery, await fixture.ReadFile(versionFile));

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task LocalCommandReadsProjectGodotAndCreatesVersionFile()
    {
        var projectPath = NewTempPath("godot-project");
        await fixture.CreateDirectory(projectPath);
        await fixture.WriteFile(Path.Combine(projectPath, "project.godot"), """
                                                                            [application]
                                                                            config/name="Test Project"
                                                                            config/features=PackedStringArray("4.6", "Forward Plus")
                                                                            """);

        var local = await fixture.ExecuteCommandInDirectory(projectPath, ["local"]);
        await fixture.AssertSuccessfulExecutionAsync(local, "local");

        var versionFile = Path.Combine(projectPath, ".fgvm-version");
        Assert.True(await fixture.FileExists(versionFile), ".fgvm-version file should be created.");
        Assert.Contains(StableVersionQuery, await fixture.ReadFile(versionFile));
        Assert.True(await fixture.HasVersionInstalled(StableVersionQuery), "Version should be installed.");

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task RemoveWithRuntimeFilterRemovesCorrectVersion()
    {
        var install = await fixture.ExecuteCommand(["install", StableVersionQuery, "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install mono");

        var remove = await fixture.ExecuteCommand(["remove", StableVersionQuery, "stable", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(remove, "remove mono");

        var listAfter = await fixture.ExecuteCommand(["list"]);
        Assert.DoesNotContain(StableMonoRelease, listAfter.Stdout, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallSetAndVerifySymlinkArtifacts()
    {
        var install = await fixture.ExecuteCommand(["install", StableRelease]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var set = await fixture.ExecuteCommand(["set", StableVersionQuery]);
        await fixture.AssertSuccessfulExecutionAsync(set, "set");

        Assert.True(await fixture.FileExists(fixture.ShimPath), "Expected fgvm shim to exist.");

        if (fixture.SupportsSymlinkAssertions)
        {
            Assert.False(await fixture.PathIsSymlink(fixture.ShimPath), "The stable PATH shim should be a generated file, not a symlink.");
            Assert.True(await fixture.PathIsSymlink(fixture.SelectedExecutablePath), "Expected selected Godot artifact symlink to exist.");

            var symlinkTarget = await fixture.ReadLink(fixture.SelectedExecutablePath);
            Assert.Contains(Path.Combine("installations", StableRelease + "-standard"), symlinkTarget);
        }

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task Arm64RunnerCanInstallAndLaunchX64NamedFixture()
    {
        if (!fixture.IsArm64)
        {
            return;
        }

        var home = NewTempPath("arch-override-x64");
        var environment = new Dictionary<string, string>
        {
            ["FGVM_HOME"] = home,
            ["FGVM_E2E_ARCH_OVERRIDE"] = "x64"
        };

        try
        {
            var install = await fixture.ExecuteCommandWithEnvironment(["install", StableRelease], environment);
            await fixture.AssertSuccessfulExecutionAsync(install, "install x64-named fixture");

            var set = await fixture.ExecuteCommandWithEnvironment(["set", StableRelease], environment);
            await fixture.AssertSuccessfulExecutionAsync(set, "set x64-named fixture");

            var godotVersion = await fixture.ExecuteCommandWithEnvironment(["godot", "--args", "--version --headless"], environment);
            await fixture.AssertSuccessfulExecutionAsync(godotVersion, "launch x64-named fixture");
            Assert.Contains(GodotMockVersion, godotVersion.Stdout);
        }
        finally
        {
            await fixture.DeletePath(home);
        }
    }

    [Fact]
    public async Task GodotCommandUsesDetachedModeWhenProjectPathContainsFlagLikeSubstrings()
    {
        await fixture.EnsureVersionInstalled(StableRelease);
        await fixture.ExecuteCommand(["set", StableRelease]);

        foreach (var projectName in new[] { "red-devil", "my-dev-project", "app-v2", "game-server", "super-quest", "hero-helper" })
        {
            var projectPath = NewTempPath(projectName);
            await fixture.CreateDirectory(projectPath);
            await fixture.WriteFile(Path.Combine(projectPath, "project.godot"), $"""
                                                                                 ; Engine configuration file.
                                                                                 config_version=5

                                                                                 [application]
                                                                                 config/name="{projectName}"
                                                                                 config/features=PackedStringArray("4.6", "Forward Plus")
                                                                                 """);

            var result = await fixture.ExecuteCommandInDirectory(projectPath, ["godot"]);

            Assert.DoesNotContain("attached mode", result.Stdout.ToLowerInvariant());
            Assert.Contains("detached mode", result.Stdout.ToLowerInvariant());
        }

        await CleanupVersion(StableRelease);
    }

    [Fact]
    public async Task InvalidVersionFailuresUseExpectedExitCodes()
    {
        var install = await fixture.ExecuteCommand(["install", "nonexistent-version-999"]);
        Assert.Equal(ExitCodes.ArgumentError, install.ExitCode);

        var local = await fixture.ExecuteCommand(["local", "nonexistent-version-999"]);
        Assert.Equal(ExitCodes.ArgumentError, local.ExitCode);

        var set = await fixture.ExecuteCommand(["set", "nonexistent-version-999"]);
        Assert.Equal(ExitCodes.GeneralError, set.ExitCode);
    }

    [Fact]
    public async Task LogsCommandDisplaysPreviousOperations()
    {
        await fixture.ExecuteCommand(["list"]);
        await fixture.ExecuteCommand(["search"]);
        await fixture.ExecuteCommand(["which"]);

        var result = await fixture.ExecuteCommand(["logs"]);

        await fixture.AssertSuccessfulExecutionAsync(result);
        Assert.True(result.Stdout.Length > 0, "Logs should not be empty after operations.");
    }

    private async Task CleanupVersion(string version)
    {
        await fixture.ExecuteCommand(["remove", version]);
        fixture.MarkVersionUninstalled(version);
    }

    private string NewTempPath(string name) =>
        Path.Combine(fixture.TempPath, $"{name}-{Guid.NewGuid():N}");

    /// <summary>
    ///     Attempts to read the project version from the .csproj file by locating it in the directory hierarchy.
    /// </summary>
    private static string GetProjectVersion()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        FileInfo? slnFile = null;
        while (dir is not null && (slnFile = dir.GetFiles("*.sln*").FirstOrDefault(f => f.Extension is ".sln" or ".slnx")) is null)
        {
            dir = dir.Parent;
        }

        if (slnFile is null)
        {
            throw new InvalidOperationException("Could not locate solution directory.");
        }

        var projectFile = Path.Combine(slnFile.DirectoryName!, "Fgvm.Cli", "Fgvm.Cli.csproj");
        var doc = XDocument.Load(projectFile);
        var version = doc.Descendants("Version").FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException("Could not locate <Version> in Fgvm.Cli.csproj.")
            : version;
    }
}
