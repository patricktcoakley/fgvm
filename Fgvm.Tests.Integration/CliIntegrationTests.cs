using System.Text.Json;
using System.Xml.Linq;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Tests.Fixtures;

namespace Fgvm.Tests.Integration;

/// <summary>
///     Collection definition to ensure integration tests run sequentially.
///     Tests share a single runner and modify shared Fgvm state.
/// </summary>
[CollectionDefinition("Integration", DisableParallelization = true)]
public class IntegrationCollection;

[Collection("Integration")]
public class CliIntegrationTests(TestFixture fixture) : IClassFixture<TestFixture>
{
    private const string StableRelease = "4.6.2-stable";
    private const string StableVersionQuery = "4.6";

    [Fact]
    public async Task DisplaysHelpWhenNoArgumentsProvided()
    {
        var result = await fixture.ExecuteCommand([]);

        await fixture.AssertSuccessfulExecutionAsync(result);
        Assert.Contains("Usage:", result.Stdout);
        Assert.Contains("Commands:", result.Stdout);
    }

    [Fact]
    public async Task GodotHelpDocumentsArgsValueSyntax()
    {
        var result = await fixture.ExecuteCommand(["godot", "--help"]);

        await fixture.AssertSuccessfulExecutionAsync(result);
        Assert.Contains("--args <string>", result.Stdout);
        Assert.Contains("Use a space after --args", result.Stdout);
        Assert.Contains("--args \"--version --verbose\"", result.Stdout);
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

        try
        {
            var result = await fixture.ExecuteCommandWithEnvironment(["list"], new Dictionary<string, string>
            {
                ["FGVM_HOME"] = customHome
            });

            await fixture.AssertSuccessfulExecutionAsync(result);
            Assert.True(await fixture.DirectoryExists(customHome), $"Expected Fgvm root to be created at '{customHome}'.");
        }
        finally
        {
            await fixture.DeletePath(customHome);
        }
    }

    [Fact]
    public async Task InvalidExportTemplateOverrideDoesNotPreventUnrelatedCommands()
    {
        var result = await fixture.ExecuteCommandWithEnvironment(["list", "--json"],
            new Dictionary<string, string>
            {
                [GodotPathService.ExportTemplatesDirectoryOverride] = "relative/export_templates"
            });

        await fixture.AssertSuccessfulExecutionAsync(result);
        using var document = JsonDocument.Parse(result.Stdout);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
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
        var releasesPath = Path.Combine(home, "releases.json");

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
        var root = home;
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
    public async Task ExportRejectsAProjectWithoutGodotFilesBeforeChangingState()
    {
        var project = NewTempPath("fgvm-export-[no]-project");

        try
        {
            await fixture.WriteFile(Path.Combine(project, "placeholder.txt"), "not a Godot project");

            var result = await fixture.ExecuteCommandInDirectory(["export", StableRelease], project);

            Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
            Assert.Contains("project.godot", result.Stderr);
            Assert.DoesNotContain("Something went wrong", result.Stderr);
            Assert.False(await fixture.FileExists(Path.Combine(project, ".fgvm-version")));
        }
        finally
        {
            await fixture.DeletePath(project);
        }
    }

    [Fact]
    public async Task ExportRejectsAManifestThatWouldOverwriteAnArtifactBeforeChangingState()
    {
        var project = NewTempPath("fgvm-export-manifest-collision");

        try
        {
            await fixture.WriteFile(Path.Combine(project, "project.godot"),
                "[application]\n\nconfig/features=PackedStringArray(\"4.6\")\n");
            await fixture.WriteFile(Path.Combine(project, "export_presets.cfg"),
                "[preset.0]\nname=\"Web\"\nplatform=\"Web\"\nexport_path=\"build/game.zip\"\n");

            var result = await fixture.ExecuteCommandInDirectory(
                ["export", "--manifest", "build/game.zip", StableRelease], project);

            Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
            Assert.Contains("conflicts", result.Stderr);
            Assert.False(await fixture.FileExists(Path.Combine(project, ".fgvm-version")));
        }
        finally
        {
            await fixture.DeletePath(project);
        }
    }

    [Fact]
    public async Task ExportJsonModeKeepsDiagnosticsOffStandardOutput()
    {
        // Native AOT publication is the only place the real serializer and the real console meet, so
        // stdout purity under --json is verified against the published binary rather than a mock.
        var project = NewTempPath("fgvm-export-[json]-purity");

        try
        {
            await fixture.WriteFile(Path.Combine(project, "project.godot"),
                "[application]\n\nconfig/features=PackedStringArray(\"4.6\")\n");
            await fixture.WriteFile(Path.Combine(project, "export_presets.cfg"),
                "[preset.0]\nname=\"Console\"\nplatform=\"Custom Console\"\nexport_path=\"\"\n");

            var result = await fixture.ExecuteCommandInDirectory(["export", "--json", StableRelease], project);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(result.Stdout.Trim());
            Assert.Contains("Console", result.Stderr);
        }
        finally
        {
            await fixture.DeletePath(project);
        }
    }

    [Fact]
    public async Task LocalRejectsMalformedExportPresetsBeforeChangingProjectState()
    {
        var project = NewTempPath("fgvm-local-[invalid]-presets");

        try
        {
            await fixture.WriteFile(Path.Combine(project, "export_presets.cfg"),
                "[preset.0]\nname=\"Web\"\nrunnable=perhaps\n");

            var result = await fixture.ExecuteCommandInDirectory(["local", StableRelease], project);

            Assert.Equal(ExitCodes.ConfigurationError, result.ExitCode);
            Assert.Contains("Configuration error", result.Stderr);
            Assert.Contains("export_presets.cfg", result.Stderr);
            Assert.Contains("line 3", result.Stderr);
            Assert.Contains("runnable", result.Stderr);
            Assert.DoesNotContain("Something went wrong", result.Stderr);
            Assert.False(await fixture.FileExists(Path.Combine(project, ".fgvm-version")));
        }
        finally
        {
            await fixture.DeletePath(project);
        }
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
    public async Task UserFacingArgumentFailuresAreNotWrappedInCatchAllMessage()
    {
        var install = await fixture.ExecuteCommand(["install", "nope"]);
        Assert.Equal(ExitCodes.ArgumentError, install.ExitCode);
        Assert.Contains("Invalid arguments: nope", install.Stderr);
        Assert.DoesNotContain("Something went wrong", install.Stderr);

        var logs = await fixture.ExecuteCommand(["logs", "--level", "verbose"]);
        Assert.Equal(ExitCodes.ArgumentError, logs.ExitCode);
        Assert.Contains("verbose is not valid", logs.Stderr);
        Assert.DoesNotContain("Something went wrong", logs.Stderr);

        var nonInteractiveInstall = await fixture.ExecuteCommand(["install"]);
        Assert.Equal(ExitCodes.ArgumentError, nonInteractiveInstall.ExitCode);
        Assert.Contains("cannot prompt because", nonInteractiveInstall.Stderr);
        Assert.Contains("not interactive", nonInteractiveInstall.Stderr);
        Assert.DoesNotContain("Something went wrong", nonInteractiveInstall.Stderr);
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

        var solutionDirectory = Assert.IsType<string>(slnFile.DirectoryName);
        var projectFile = Path.Combine(solutionDirectory, "Fgvm.Cli", "Fgvm.Cli.csproj");
        var doc = XDocument.Load(projectFile);
        var version = doc.Descendants("Version").FirstOrDefault()?.Value;
        return string.IsNullOrWhiteSpace(version)
            ? throw new InvalidOperationException("Could not locate <Version> in Fgvm.Cli.csproj.")
            : version;
    }
}
