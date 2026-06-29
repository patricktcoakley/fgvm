using System.Text.Json;
using System.Xml.Linq;
using Fgvm.Error;

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
