using Fgvm.Error;
using System.Text.Json;
using System.Xml.Linq;

namespace Fgvm.Tests.EndToEnd;

/// <summary>
///     Collection definition to ensure E2E tests run sequentially.
///     Tests share a single container and modify shared Fgvm state.
/// </summary>
[CollectionDefinition("EndToEnd", DisableParallelization = true)]
public class EndToEndCollection;

[Collection("EndToEnd")]
public class EndToEndTests(TestContainerFixture fixture) : IClassFixture<TestContainerFixture>
{
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
    public async Task CreatesFgvmDirectoryOnFirstCommand()
    {
        await fixture.ExecuteCommand(["list"]);

        var directoryExists = await fixture.DirectoryExists("/root/fgvm");
        Assert.True(directoryExists);
    }

    [Fact]
    public async Task UsesFgvmHomeEnvironmentVariableForRootPath()
    {
        const string customHome = "/tmp/fgvm-env-test";
        var fgvmRoot = $"{customHome}/fgvm";
        var fgvmPath = fixture.FgvmPath;

        await fixture.ExecuteShellCommand("rm", ["-rf", customHome]);

        var result = await fixture.ExecuteShellCommand("sh", ["-c", $"FGVM_HOME={customHome} {fgvmPath} list"]);
        await fixture.AssertSuccessfulExecutionAsync(result);

        var directoryExists = await fixture.DirectoryExists(fgvmRoot);
        Assert.True(directoryExists, $"Expected Fgvm root to be created at '{fgvmRoot}' when FGVM_HOME is set.");

        await fixture.ExecuteShellCommand("rm", ["-rf", customHome]);
    }

    [Fact]
    public async Task DoesNotCreateDefaultRootWhenFgvmHomeIsSet()
    {
        const string customHome = "/tmp/fgvm-env-test-no-default";
        var defaultRoot = "/root/fgvm";
        var fgvmPath = fixture.FgvmPath;

        await fixture.ExecuteShellCommand("rm", ["-rf", defaultRoot]);
        await fixture.ExecuteShellCommand("rm", ["-rf", customHome]);

        var result = await fixture.ExecuteShellCommand("sh", ["-c", $"FGVM_HOME={customHome} {fgvmPath} list"]);
        await fixture.AssertSuccessfulExecutionAsync(result);

        var defaultExists = await fixture.DirectoryExists(defaultRoot);
        Assert.False(defaultExists, $"Default root '{defaultRoot}' should not be recreated when FGVM_HOME is provided.");

        await fixture.ExecuteShellCommand("rm", ["-rf", defaultRoot]);
        await fixture.ExecuteShellCommand("rm", ["-rf", customHome]);
    }

    [Fact]
    public async Task DisplaysHelpForUnrecognizedCommands()
    {
        var result = await fixture.ExecuteCommand(["nonexistent-command"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.Stdout);
    }

    [Fact]
    public async Task RecordsCommandExecutionsInLogs()
    {
        await fixture.ExecuteCommand(["list"]);
        await fixture.ExecuteCommand(["which"]);

        var logResult = await fixture.ExecuteCommand(["logs"]);
        Assert.Equal(0, logResult.ExitCode);
        Assert.True(logResult.Stdout.Length > 0, "Logs should not be empty after operations");
    }

    [Fact]
    public async Task SearchCommandExecutesWithoutCrashing()
    {
        var result = await fixture.ExecuteCommand(["search"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }

    [Fact]
    public async Task SearchCommandSupportsJsonOutput()
    {
        var result = await fixture.ExecuteCommand(["search", "--json", "4.5"]);

        await fixture.AssertSuccessfulExecutionAsync(result);

        using var document = JsonDocument.Parse(result.Stdout.Trim());
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.True(document.RootElement.GetArrayLength() > 0);
    }

    [Fact]
    public async Task SearchCommandCreatesReleasesJsonWhenMissing()
    {
        var home = $"/tmp/fgvm-cache-missing-{Guid.NewGuid():N}";
        var root = $"{home}/fgvm";
        var releasesPath = $"{root}/releases.json";

        try
        {
            await fixture.ExecuteShellCommand("rm", ["-rf", home]);

            var result = await fixture.ExecuteShellCommand("sh", ["-c", $"FGVM_HOME={home} {fixture.FgvmPath} search --json 4.5"]);

            await fixture.AssertSuccessfulExecutionAsync(result, "search");
            Assert.True(await fixture.FileExists(releasesPath), "Expected search to create releases.json when no cache exists.");

            var content = await fixture.ReadFile(releasesPath);
            using var document = JsonDocument.Parse(content);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Contains(document.RootElement.EnumerateObject(), property => property.Value.TryGetProperty("stable", out _));
        }
        finally
        {
            await fixture.ExecuteShellCommand("rm", ["-rf", home]);
        }
    }

    [Fact]
    public async Task SearchCommandNoCacheCreatesReleasesJsonWhenMissing()
    {
        var home = $"/tmp/fgvm-no-cache-missing-{Guid.NewGuid():N}";
        var root = $"{home}/fgvm";
        var releasesPath = $"{root}/releases.json";

        try
        {
            await fixture.ExecuteShellCommand("rm", ["-rf", home]);

            var result = await fixture.ExecuteShellCommand("sh", ["-c", $"FGVM_HOME={home} {fixture.FgvmPath} search --no-cache --json 4.5"]);

            await fixture.AssertSuccessfulExecutionAsync(result, "search --no-cache");
            Assert.True(await fixture.FileExists(releasesPath), "Expected --no-cache search to create releases.json when no cache exists.");

            var content = await fixture.ReadFile(releasesPath);
            using var document = JsonDocument.Parse(content);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Contains(document.RootElement.EnumerateObject(), property => property.Value.TryGetProperty("stable", out _));
        }
        finally
        {
            await fixture.ExecuteShellCommand("rm", ["-rf", home]);
        }
    }

    [Fact]
    public async Task SearchCommandNoCacheRefreshesSeededReleasesJson()
    {
        var home = $"/tmp/fgvm-no-cache-refresh-{Guid.NewGuid():N}";
        var root = $"{home}/fgvm";
        var releasesPath = $"{root}/releases.json";

        try
        {
            await fixture.ExecuteShellCommand("rm", ["-rf", home]);
            await fixture.ExecuteShellCommand("mkdir", ["-p", root]);
            await fixture.ExecuteShellCommand("sh", [
                "-c",
                $"cat > {releasesPath} << 'EOF'\n{{\"4.999\":{{\"stable\":{{}}}}}}\nEOF"
            ]);

            var cachedSearch = await fixture.ExecuteShellCommand("sh", ["-c", $"FGVM_HOME={home} {fixture.FgvmPath} search --json 4.999"]);
            await fixture.AssertSuccessfulExecutionAsync(cachedSearch, "cached search");
            Assert.Contains("4.999-stable", cachedSearch.Stdout);

            var refreshedSearch = await fixture.ExecuteShellCommand("sh", ["-c", $"FGVM_HOME={home} {fixture.FgvmPath} search --no-cache --json 4.999"]);
            await fixture.AssertSuccessfulExecutionAsync(refreshedSearch, "search --no-cache");
            Assert.DoesNotContain("4.999-stable", refreshedSearch.Stdout);

            var content = await fixture.ReadFile(releasesPath);
            Assert.DoesNotContain("4.999-stable", content);

            using var document = JsonDocument.Parse(content);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.Contains(document.RootElement.EnumerateObject(), property => property.Value.TryGetProperty("stable", out _));
        }
        finally
        {
            await fixture.ExecuteShellCommand("rm", ["-rf", home]);
        }
    }

    [Fact]
    public async Task ListCommandDisplaysOutput()
    {
        var result = await fixture.ExecuteCommand(["list"]);

        await fixture.AssertSuccessfulExecutionAsync(result);
        Assert.True(result.Stdout.Length > 0);
    }

    [Fact]
    public async Task ListCommandSupportsJsonOutput()
    {
        var result = await fixture.ExecuteCommand(["list", "--json"]);

        await fixture.AssertSuccessfulExecutionAsync(result);

        using var document = JsonDocument.Parse(result.Stdout.Trim());
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }

    [Fact]
    public async Task WhichCommandSucceedsWithoutActiveVersion()
    {
        var result = await fixture.ExecuteCommand(["which"]);

        await fixture.AssertSuccessfulExecutionAsync(result);
    }

    [Fact]
    public async Task WhichCommandSupportsJsonOutput()
    {
        var result = await fixture.ExecuteCommand(["which", "--json"]);

        await fixture.AssertSuccessfulExecutionAsync(result);

        using var document = JsonDocument.Parse(result.Stdout.Trim());
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.True(document.RootElement.TryGetProperty("hasVersion", out var hasVersion));
        if (hasVersion.GetBoolean())
        {
            Assert.True(document.RootElement.TryGetProperty("symlinkPath", out var symlinkPath));
            Assert.False(string.IsNullOrWhiteSpace(symlinkPath.GetString()));
        }
    }

    [Fact]
    public async Task AllCommandsDisplayHelpWithHelpFlag()
    {
        var commands = new[] { "install", "remove", "set", "local", "search", "list", "which", "logs", "godot" };

        foreach (var command in commands)
        {
            var result = await fixture.ExecuteCommand([command, "--help"]);
            Assert.Equal(0, result.ExitCode);
            Assert.Contains(command, result.Stdout, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task InstallCommandFailsGracefullyForInvalidVersion()
    {
        var result = await fixture.ExecuteCommand(["install", "nonexistent-version-999"]);

        Assert.Equal(ExitCodes.ArgumentError, result.ExitCode);
        await fixture.AssertLogContains("install");
    }

    [Fact]
    public async Task RemoveCommandHandlesInvalidVersionGracefully()
    {
        var result = await fixture.ExecuteCommand(["remove", "nonexistent-version-999"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }

    [Fact]
    public async Task RemoveWithRuntimeFilter_RemovesCorrectVersion()
    {
        // Install mono version
        var installMono = await fixture.ExecuteCommand(["install", "4.3", "stable", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(installMono, "install mono");

        // Verify it's installed
        var listBefore = await fixture.ExecuteCommand(["list"]);
        Assert.Contains("4.3", listBefore.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mono", listBefore.Stdout, StringComparison.OrdinalIgnoreCase);

        // Remove with specific version and runtime query to match exactly one
        var remove = await fixture.ExecuteCommand(["remove", "4.3", "stable", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(remove, "remove 4.3 stable mono");

        // Verify it's removed
        var listAfter = await fixture.ExecuteCommand(["list"]);
        // The 4.3 mono version should be gone (but other versions may exist)
        Assert.False(listAfter.Stdout.Contains("4.3-stable-mono", StringComparison.OrdinalIgnoreCase),
            "4.3-stable-mono should be removed");
    }

    [Fact]
    public async Task RemoveWithDevAndMonoQuery_RemovesCorrectly()
    {
        // Install a dev version with mono runtime
        var install = await fixture.ExecuteCommand(["install", "dev", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install dev mono");

        // Get the exact version that was installed
        var listBefore = await fixture.ExecuteCommand(["list"]);
        Assert.Contains("dev", listBefore.Stdout, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mono", listBefore.Stdout, StringComparison.OrdinalIgnoreCase);

        // Remove with dev and mono query - should match exactly one (the one we just installed)
        var remove = await fixture.ExecuteCommand(["remove", "dev", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(remove, "remove dev mono");

        // Verify dev mono is no longer in the list
        var listAfter = await fixture.ExecuteCommand(["list"]);
        // Should not have any dev+mono combination
        var lines = listAfter.Stdout.Split('\n');
        var hasDevMono = lines.Any(line =>
            line.Contains("dev", StringComparison.OrdinalIgnoreCase) &&
            line.Contains("mono", StringComparison.OrdinalIgnoreCase));

        Assert.False(hasDevMono, "Dev mono version should be removed");
    }

    [Fact]
    public async Task SetCommandFailsGracefullyForInvalidVersion()
    {
        var result = await fixture.ExecuteCommand(["set", "nonexistent-version-999"]);

        Assert.Equal(ExitCodes.GeneralError, result.ExitCode);
    }

    [Fact]
    public async Task LocalCommandHandlesInvalidVersionGracefully()
    {
        var result = await fixture.ExecuteCommand(["local", "nonexistent-version-999"]);

        Assert.Equal(ExitCodes.ArgumentError, result.ExitCode);
    }

    [Fact]
    public async Task SearchCommandAcceptsVersionQueryParameter()
    {
        var result = await fixture.ExecuteCommand(["search", "4.5"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
        Assert.True(result.Stdout.Length > 0);
    }

    [Fact]
    public async Task GodotCommandShowsUsageWithoutArguments()
    {
        var result = await fixture.ExecuteCommand(["godot"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }

    [Fact]
    public async Task GodotCommandPassesThroughArguments()
    {
        var result = await fixture.ExecuteCommand(["godot", "--version"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }


    [Fact]
    public async Task LocalCommandCreatesVersionFileInCurrentDirectory()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/local-test"]);
        var result = await fixture.ExecuteCommandInDirectory("/tmp/local-test", ["local", "4.5-stable"]);

        Assert.True(result.ExitCode == ExitCodes.Success, $"Expected success but got exit code {result.ExitCode}. STDOUT: {result.Stdout}, STDERR: {result.Stderr}");

        var fileExists = await fixture.FileExists("/tmp/local-test/.fgvm-version");
        Assert.True(fileExists, "Expected .fgvm-version file to be created after successful local command");

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task LocalCommandCreatesVersionFileWithCorrectContent()
    {
        await fixture.EnsureVersionInstalled("4.5-stable");

        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/local-content-test"]);
        var result = await fixture.ExecuteCommandInDirectory("/tmp/local-content-test", ["local", "4.5-stable"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");

        var fileContent = await fixture.ReadFile("/tmp/local-content-test/.fgvm-version");
        Assert.Contains("4.5", fileContent);
        Assert.Contains("stable", fileContent);

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task SetCommandUpdatesGlobalVersion()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var install = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var result = await fixture.ExecuteCommand(["set", "4.5"]);
        await fixture.AssertSuccessfulExecutionAsync(result, "result");

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task RemoveCommandHandlesNonInstalledVersionGracefully()
    {
        var result = await fixture.ExecuteCommand(["remove", "99.99.99-invalid"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }

    [Fact]
    public async Task ListCommandWithInstalledFilter()
    {
        var result = await fixture.ExecuteCommand(["list", "--installed"]);

        Assert.Equal(ExitCodes.ArgumentError, result.ExitCode);
    }

    [Fact]
    public async Task SearchCommandHandlesNetworkFailuresGracefully()
    {
        var result = await fixture.ExecuteCommand(["search", "nonexistent-query-that-wont-match-anything"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }

    [Fact]
    public async Task LogsCommandDisplaysPreviousOperations()
    {
        await fixture.ExecuteCommand(["list"]);
        await fixture.ExecuteCommand(["search"]);
        await fixture.ExecuteCommand(["which"]);

        var result = await fixture.ExecuteCommand(["logs"]);

        await fixture.AssertSuccessfulExecutionAsync(result);
        Assert.True(result.Stdout.Length > 0);
    }

    [Fact]
    public async Task MultipleSequentialInstallsWorkCorrectly()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var install1 = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install1, "install1");

        var install2 = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install2, "install2");

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task FullVersionManagementWorkflow()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        Assert.Contains("4.5-stable", searchResult.Stdout);

        var installResult = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(installResult, "installResult");

        Assert.True(await fixture.HasVersionInstalled("4.5"),
            "Expected 4.5 stable version to be installed");

        // Set it as the default
        await fixture.ExecuteCommand(["set", "4.5"]);

        var currentVersion = await fixture.GetCurrentVersion();
        Assert.Contains("4.5", currentVersion);

        var install2Result = await fixture.ExecuteCommand(["install", "4.5-rc2"]);
        await fixture.AssertSuccessfulExecutionAsync(install2Result, "install2Result");

        Assert.True(await fixture.HasVersionInstalled("4.5"),
            "Expected 4.5 stable version to still be installed");

        Assert.True(await fixture.HasVersionInstalled("rc2"),
            "Expected 4.5 rc2 version to be installed");

        // The install of 4.5-rc2 may find a patch version like 4.5.1-rc2
        // Query with just rc2 should find any rc2 version installed
        var setResult = await fixture.ExecuteCommand(["set", "rc2"]);
        await fixture.AssertSuccessfulExecutionAsync(setResult);

        var newCurrentVersion = await fixture.GetCurrentVersion();
        Assert.Contains("rc2", newCurrentVersion);

        var fgvmPath = fixture.FgvmPath;
        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/test-project"]);
        var localSetResult = await fixture.ExecuteShellCommand("sh", [
            "-c",
            $"cd /tmp/test-project && {fgvmPath} local 4.5-stable"
        ]);

        if (localSetResult.ExitCode == ExitCodes.Success)
        {
            var versionFileExists = await fixture.FileExists("/tmp/test-project/.fgvm-version");
            Assert.True(versionFileExists, "Expected .fgvm-version file to be created after successful local command");

            var localResult = await fixture.ExecuteShellCommand("sh", [
                "-c",
                $"cd /tmp/test-project && {fgvmPath} which"
            ]);

            Assert.Contains("4.5", localResult.Stdout);
        }

        await CleanupVersion("4.5-rc2");
        await CleanupVersion("4.5-stable");

        var finalListResult = await fixture.ExecuteCommand(["list"]);
        await fixture.AssertSuccessfulExecutionAsync(finalListResult);

        var logs = await fixture.GetLogs();
        Assert.True(logs.Length > 0, "Should have generated logs during workflow");
        await fixture.AssertLogContains("install");
    }

    private async Task CleanupVersion(string version)
    {
        await fixture.ExecuteCommand(["remove", version]);
        TestHelpers.MarkVersionUninstalled(version);
    }

    [Fact]
    public async Task HandlesInvalidVersionFormatGracefully()
    {
        var fgvmPath = fixture.FgvmPath;
        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/invalid-version"]);
        await fixture.ExecuteShellCommand("sh", ["-c", "echo 'not-a-valid-version-123' > /tmp/invalid-version/.fgvm-version"]);

        // Try to use local command which should validate the version
        var localResult = await fixture.ExecuteShellCommand("sh", ["-c", $"cd /tmp/invalid-version && {fgvmPath} local"]);

        // Should fail with GeneralError since the .fgvm-version contains invalid format
        Assert.Equal(ExitCodes.GeneralError, localResult.ExitCode);
    }

    [Fact]
    public async Task GodotCommandHandlesNoVersionSetGracefully()
    {
        var fgvmPath = fixture.FgvmPath;
        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/no-version"]);

        var result = await fixture.ExecuteShellCommand("sh", ["-c", $"cd /tmp/no-version && {fgvmPath} godot \"--version\""]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");
    }

    [Fact]
    public async Task GodotCommandHandlesComplexArguments()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var install = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        await fixture.ExecuteCommand(["set", "4.5-stable"]);

        var result = await fixture.ExecuteCommand(["godot", "--args", "--help"]);

        await fixture.AssertSuccessfulExecutionAsync(result, "result");

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task InstallingSameVersionTwiceIsIdempotent()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "search");

        var install1 = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install1, "install 4.5-stable (first)");

        var install2 = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install2, "install 4.5-stable (second)");

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task InstallCommandWithInvalidVersionFails()
    {
        var result = await fixture.ExecuteCommand(["install", "999.999-invalid"]);

        Assert.Equal(ExitCodes.ArgumentError, result.ExitCode);
    }

    [Fact]
    public async Task RemovingGloballySetVersionSucceeds()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var install = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        await fixture.ExecuteCommand(["set", "4.5-stable"]);
        var remove = await fixture.ExecuteCommand(["remove", "4.5-stable"]);

        await fixture.AssertSuccessfulExecutionAsync(remove, "remove");
    }

    [Fact]
    public async Task RemovingLocallySetVersionSucceeds()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var install = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var fgvmPath = fixture.FgvmPath;
        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/remove-local"]);
        await fixture.ExecuteShellCommand("sh", ["-c", $"cd /tmp/remove-local && {fgvmPath} local 4.5-stable"]);

        var remove = await fixture.ExecuteCommand(["remove", "4.5-stable"]);

        await fixture.AssertSuccessfulExecutionAsync(remove, "remove");
    }

    [Fact]
    public async Task SetCommandWithNonExistentVersionFails()
    {
        var result = await fixture.ExecuteCommand(["set", "999.999-nonexistent"]);

        Assert.Equal(ExitCodes.GeneralError, result.ExitCode);
    }

    [Fact]
    public async Task SetCommandAcceptsMultipleArgumentsForStandard()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5", "standard"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var result = await fixture.ExecuteCommand(["set", "4.5", "standard"]);
        await fixture.AssertSuccessfulExecutionAsync(result, "set with standard runtime");

        Assert.True(await fixture.HasVersionInstalled("standard"),
            "Standard runtime version not found in installed versions");

        await CleanupVersion("4.5-stable-standard");
    }

    [Fact]
    public async Task SetCommandAcceptsMultipleArgumentsForMono()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var result = await fixture.ExecuteCommand(["set", "4.5", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(result, "set with mono runtime");

        Assert.True(await fixture.HasVersionInstalled("mono"),
            "Mono runtime version not found in installed versions");

        await CleanupVersion("4.5-stable-mono");
    }

    [Fact]
    public async Task SetCommandWithSingleVersionArgument()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var result = await fixture.ExecuteCommand(["set", "4.5"]);
        await fixture.AssertSuccessfulExecutionAsync(result, "set with version only");

        var whichResult = await fixture.ExecuteCommand(["which"]);
        Assert.Contains("4.5", whichResult.Stdout);

        await CleanupVersion("4.5-stable-standard");
    }

    [Fact]
    public async Task LocalCommandWithNonExistentVersionFails()
    {
        var result = await fixture.ExecuteCommand(["local", "999.999-nonexistent"]);

        Assert.Equal(ExitCodes.ArgumentError, result.ExitCode);
    }

    [Fact]
    public async Task LocalCommandAcceptsMultipleArgumentsForStandard()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5", "standard"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/local-multi-standard"]);
        var result = await fixture.ExecuteCommandInDirectory("/tmp/local-multi-standard", ["local", "4.5", "standard"]);
        await fixture.AssertSuccessfulExecutionAsync(result, "local with standard runtime");

        var fileExists = await fixture.FileExists("/tmp/local-multi-standard/.fgvm-version");
        Assert.True(fileExists, "Version file was not created");

        await CleanupVersion("4.5-stable-standard");
    }

    [Fact]
    public async Task LocalCommandAcceptsMultipleArgumentsForMono()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/local-multi-mono"]);
        var result = await fixture.ExecuteCommandInDirectory("/tmp/local-multi-mono", ["local", "4.5", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(result, "local with mono runtime");

        var fileExists = await fixture.FileExists("/tmp/local-multi-mono/.fgvm-version");
        Assert.True(fileExists, "Version file was not created");

        await CleanupVersion("4.5-stable-mono");
    }

    [Fact]
    public async Task LogsCommandWorksWithNoOperations()
    {
        var result = await fixture.ExecuteCommand(["logs"]);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task LogsCommandShowsRecentOperationsInOrder()
    {
        var searchResult = await fixture.ExecuteCommand(["search"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var install = await fixture.ExecuteCommand(["install", "4.5-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        var set = await fixture.ExecuteCommand(["set", "4.5"]);
        await fixture.AssertSuccessfulExecutionAsync(set, "set");

        var list = await fixture.ExecuteCommand(["list"]);
        await fixture.AssertSuccessfulExecutionAsync(list, "list");

        var logs = await fixture.GetLogs();

        Assert.False(string.IsNullOrEmpty(logs));
        Assert.Contains("install", logs.ToLower());
        Assert.Contains("set", logs.ToLower());

        await CleanupVersion("4.5-stable");
    }

    [Fact]
    public async Task InstallingGodot3StandardWorksOnBothArchitectures()
    {
        // v3.6-stable standard works on both x64 (x11.64) and arm64 (linux.arm64)
        // When installing 3.6-stable, it will find the latest patch version (e.g., 3.6.1-stable)
        var install = await fixture.ExecuteCommand(["install", "3.6-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        // Check that some version of 3.6 stable was installed (could be 3.6-stable or 3.6.1-stable, etc.)
        Assert.True(await fixture.HasVersionInstalled("3.6"),
            "Version 3.6 not found in installed versions");

        await CleanupVersion("3.6-stable");
    }

    [Fact]
    public async Task InstalledGodotBinaryIsExecutable()
    {
        var install = await fixture.ExecuteCommand(["install", "4.3-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        await fixture.ExecuteCommand(["set", "4.3-stable"]);

        // Run Godot in headless mode to verify it's actually executable
        var godotVersion = await fixture.ExecuteCommand(["godot", "--", "--version", "--headless"]);
        await fixture.AssertSuccessfulExecutionAsync(godotVersion, "godotVersion");
        Assert.Contains("4.3", godotVersion.Stdout);

        await CleanupVersion("4.3-stable");
    }

    [Fact]
    public async Task InstallingMonoRuntimeWorks()
    {
        var install = await fixture.ExecuteCommand(["install", "4.5", "mono"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        Assert.True(await fixture.HasVersionInstalled("mono"),
            "Mono runtime version not found in installed versions");

        await fixture.ExecuteCommand(["set", "4.5", "mono"]);

        var godotVersion = await fixture.ExecuteCommand(["godot", "--", "--version", "--headless"]);
        Assert.Equal(0, godotVersion.ExitCode);

        await fixture.ExecuteCommand(["remove", "latest", "mono"]);
    }

    [Fact]
    public async Task SearchCommandDisplaysChronologicalOrdering()
    {
        var searchResult = await fixture.ExecuteCommand(["search", "4"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        // Parse output by removing panel borders and extracting version lines
        var lines = searchResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("4.") && line.Contains("-"))
            .Select(line => line.Replace("│", "").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        // Verify we have some results
        Assert.NotEmpty(lines);

        // Find indices of specific versions to verify chronological ordering
        // Chronological means: higher versions first, then stability within each version
        var stableIndex = Array.FindIndex(lines, l => l.Contains("4.5-stable") || l.Contains("4.4-stable"));
        var rcIndex = Array.FindIndex(lines, l => l.Contains("-rc"));

        // If we have both stable and rc versions, stable of same/higher version should come before rc
        if (stableIndex >= 0 && rcIndex >= 0)
        {
            // Extract version numbers to compare
            var stableLine = lines[stableIndex];
            var rcLine = lines[rcIndex];

            // If they're the same major.minor version, stable should come first
            if (stableLine.StartsWith("4.") && rcLine.StartsWith("4."))
            {
                var stableVersion = stableLine.Split('-')[0];
                var rcVersion = rcLine.Split('-')[0];

                if (stableVersion == rcVersion)
                {
                    Assert.True(stableIndex < rcIndex,
                        $"Chronological ordering failed: {stableLine} (index {stableIndex}) should appear before {rcLine} (index {rcIndex})");
                }
            }
        }
    }

    [Fact]
    public async Task InstallCommandUsesStabilityFirstSelection()
    {
        // This test verifies that when multiple versions match a query,
        // install prefers stable versions over newer unstable versions

        // First, check what versions are available
        var searchResult = await fixture.ExecuteCommand(["search", "4"]);
        await fixture.AssertSuccessfulExecutionAsync(searchResult, "searchResult");

        var hasStable = searchResult.Stdout.Contains("-stable");
        var hasDev = searchResult.Stdout.Contains("-dev");

        if (!hasStable || !hasDev)
        {
            return; // Skip if we don't have both stable and dev versions
        }

        var installResult = await fixture.ExecuteCommand(["install", "4"]);
        await fixture.AssertSuccessfulExecutionAsync(installResult, "installResult");

        var listResult = await fixture.ExecuteCommand(["list"]);
        await fixture.AssertSuccessfulExecutionAsync(listResult, "listResult");

        // Should have installed a stable version, not a dev version
        var installedStable = listResult.Stdout.Contains("-stable");
        var installedDev = listResult.Stdout.Contains("-dev");

        Assert.True(installedStable && !installedDev,
            $"Install with query '4' should prefer stable version over dev. Installed versions: {listResult.Stdout}");

        // Cleanup
        await CleanupVersion("4");
    }

    [Fact]
    public async Task InstallSetAndVerifyWithWhich()
    {
        // Install a version
        var install = await fixture.ExecuteCommand(["install", "4.3-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install, "install");

        // Set it as the global default
        var set = await fixture.ExecuteCommand(["set", "4.3"]);
        await fixture.AssertSuccessfulExecutionAsync(set, "set");

        // Verify which shows it as the default
        var which = await fixture.ExecuteCommand(["which"]);
        await fixture.AssertSuccessfulExecutionAsync(which, "which");
        Assert.Contains("4.3", which.Stdout);

        // Cleanup
        await CleanupVersion("4.3-stable");
        await fixture.ExecuteShellCommand("rm", ["-f", "/root/fgvm/.version"]);
    }

    [Fact]
    public async Task FirstVersionBecomesDefaultSecondDoesNot()
    {
        // Clear any existing default
        await fixture.ExecuteShellCommand("rm", ["-f", "/root/fgvm/.version"]);

        // Install first version
        var install1 = await fixture.ExecuteCommand(["install", "4.2-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install1, "install1");

        // Explicitly set it as default (install doesn't auto-set)
        var set1 = await fixture.ExecuteCommand(["set", "4.2"]);
        await fixture.AssertSuccessfulExecutionAsync(set1, "set1");

        // Verify it's the default
        var which1 = await fixture.ExecuteCommand(["which"]);
        await fixture.AssertSuccessfulExecutionAsync(which1, "which1");
        Assert.Contains("4.2", which1.Stdout);

        // Install second version without setting it
        var install2 = await fixture.ExecuteCommand(["install", "4.3-stable"]);
        await fixture.AssertSuccessfulExecutionAsync(install2, "install2");

        // Verify first version is still default
        var which2 = await fixture.ExecuteCommand(["which"]);
        await fixture.AssertSuccessfulExecutionAsync(which2, "which2");
        Assert.Contains("4.2", which2.Stdout);

        // Cleanup
        await CleanupVersion("4.2-stable");
        await CleanupVersion("4.3-stable");
        await fixture.ExecuteShellCommand("rm", ["-f", "/root/fgvm/.version"]);
    }

    [Fact]
    public async Task LocalCommandReadsProjectGodotAndCreatesVersionFile()
    {
        // Create a project directory with a project.godot file
        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/godot-project"]);

        var projectContent = """
                             [application]
                             config/name="Test Project"
                             config/features=PackedStringArray("4.3", "Forward Plus")
                             """;

        await fixture.ExecuteShellCommand("sh", ["-c", $"cat > /tmp/godot-project/project.godot << 'EOF'\n{projectContent}\nEOF"]);

        // Run local command in that directory (should detect 4.3 from project.godot)
        var local = await fixture.ExecuteCommandInDirectory("/tmp/godot-project", ["local"]);
        await fixture.AssertSuccessfulExecutionAsync(local, "local");

        // Verify .fgvm-version was created
        var versionFileExists = await fixture.FileExists("/tmp/godot-project/.fgvm-version");
        Assert.True(versionFileExists, ".fgvm-version file should be created");

        // Verify it contains the correct version
        var versionContent = await fixture.ReadFile("/tmp/godot-project/.fgvm-version");
        Assert.Contains("4.3", versionContent);
        Assert.Contains("stable", versionContent);

        // Verify the version was installed
        var hasVersion = await fixture.HasVersionInstalled("4.3");
        Assert.True(hasVersion, "Version 4.3 should be installed");

        await CleanupVersion("4.3-stable");
    }

    [Fact]
    public async Task LocalCommandFailsWhenProjectVersionDoesNotExistAndNoVersionsInstalled()
    {
        // Ensure no versions are installed by removing all versions
        var list = await fixture.ExecuteCommand(["list"]);
        if (list.ExitCode == 0 && list.Stdout.Contains("4."))
        {
            // Remove any 4.x versions that might exist
            await fixture.ExecuteCommand(["remove", "4"]);
        }

        // Create a project directory with a project.godot file requesting a non-existent version
        await fixture.ExecuteShellCommand("mkdir", ["-p", "/tmp/godot-project-invalid"]);

        var projectContent = """
                             [application]
                             config/name="Test Project"
                             config/features=PackedStringArray("999.999", "Forward Plus")
                             """;

        await fixture.ExecuteShellCommand("sh", ["-c", $"cat > /tmp/godot-project-invalid/project.godot << 'EOF'\n{projectContent}\nEOF"]);

        // Run local command - should fail because version 999.999 doesn't exist and no versions are installed
        var local = await fixture.ExecuteCommandInDirectory("/tmp/godot-project-invalid", ["local"]);

        // Should exit with error (not success)
        Assert.NotEqual(ExitCodes.Success, local.ExitCode);
    }

    [Fact]
    public async Task GodotCommandUsesDetachedModeWhenProjectPathContainsFlagLikeSubstrings()
    {
        // Install and set a version once
        await fixture.EnsureVersionInstalled("4.4-stable");
        await fixture.ExecuteCommand(["set", "4.4"]);

        // Test multiple project names that contain flag-like substrings
        var projectNames = new[]
        {
            "red-devil", // Contains -d
            "my-dev-project", // Contains -dev
            "app-v2", // Contains -v
            "game-server", // Contains -s
            "super-quest", // Contains -q
            "hero-helper" // Contains -h
        };

        foreach (var projectName in projectNames)
        {
            // Create project with name containing flag-like substrings
            var projectPath = $"/tmp/{projectName}";
            await fixture.ExecuteShellCommand("mkdir", ["-p", projectPath]);

            var projectContent = $"""
                                  ; Engine configuration file.
                                  config_version=5

                                  [application]
                                  config/name="{projectName}"
                                  config/features=PackedStringArray("4.4", "Forward Plus")
                                  """;

            await fixture.ExecuteShellCommand("sh", [
                "-c",
                $"cat > {projectPath}/project.godot << 'EOF'\n{projectContent}\nEOF"
            ]);

            // Run fgvm godot in that directory
            var result = await fixture.ExecuteCommandInDirectory(projectPath, ["godot"]);

            // Should use DETACHED mode, not attached
            Assert.DoesNotContain("attached mode", result.Stdout.ToLower());
            Assert.Contains("detached mode", result.Stdout.ToLower());
        }

        // Cleanup once
        await CleanupVersion("4.4-stable");
    }

    /// <summary>
    ///     Attempts to read the project version from the .csproj file by locating it in the directory hierarchy.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
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

        var csproj = dir?.GetFiles("*.csproj", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Name.Equals("Fgvm.Cli.csproj", StringComparison.OrdinalIgnoreCase));

        if (csproj is null)
        {
            throw new InvalidOperationException("Could not locate project file.");
        }

        var doc = XDocument.Load(csproj.FullName);
        var version = doc.Descendants("Version").FirstOrDefault()?.Value
                      ?? throw new InvalidOperationException("Version element not found.");

        return version;
    }
}
