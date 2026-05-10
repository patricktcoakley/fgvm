using Fgvm.Cli.Command;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;
using System.Text.Json;

namespace Fgvm.Tests.Command;

public sealed class LogsCommandTests : IDisposable
{
    private readonly string _tempRoot;

    public LogsCommandTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "fgvm-logs-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch
        {
            // best effort cleanup
        }
    }

    [Fact]
    public async Task LogsCommand_WritesHumanReadableOutput()
    {
        var timestamp = DateTime.UtcNow;
        var logLines = new[]
        {
            $$"""{"Timestamp":"{{timestamp:O}}","LogLevel":"Information","Message":"Operation completed","Category":"Fgvm.Test.Component"}""",
            "malformed entry"
        };

        var (command, console) = CreateCommand(logLines);

        await command.Logs(cancellationToken: CancellationToken.None);

        var output = console.Output;
        Assert.Contains("Timestamp:", output);
        Assert.Contains("LogLevel: Information", output);
        Assert.Contains("Fgvm.Test.Component", output);
        Assert.Contains("Skipped 1 malformed log entries.", output);
    }

    [Fact]
    public async Task LogsCommand_WritesJsonOutput()
    {
        var first = DateTime.UtcNow;
        var second = first.AddSeconds(1);
        var logLines = new[]
        {
            $$"""{"Timestamp":"{{first:O}}","LogLevel":"Warning","Message":"First message","Category":"Fgvm.Json.Category"}""",
            $$"""{"Timestamp":"{{second:O}}","LogLevel":"Information","Message":"Second message with | pipe","Category":"Fgvm.Json.Other"}"""
        };

        var (command, console) = CreateCommand(logLines);

        await command.Logs(true, cancellationToken: CancellationToken.None);

        var json = console.Output.Trim();
        Assert.False(string.IsNullOrWhiteSpace(json));

        var entries = JsonSerializer.Deserialize<List<LogEntryView>>(json, JsonView.Options);
        Assert.NotNull(entries);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Level == "Warning" && e.Category == "Fgvm.Json.Category");
    }

    [Fact]
    public async Task LogsCommand_AppliesFilters()
    {
        var timestamp = DateTime.UtcNow;
        var logLines = new[]
        {
            $$"""{"Timestamp":"{{timestamp:O}}","LogLevel":"Information","Message":"Download completed","Category":"Fgvm.Filter.Component"}""",
            $$"""{"Timestamp":"{{timestamp.AddSeconds(1):O}}","LogLevel":"Warning","Message":"Cache miss","Category":"Fgvm.Filter.Component"}"""
        };

        var (command, console) = CreateCommand(logLines);

        await command.Logs(level: "warn", cancellationToken: CancellationToken.None);

        var output = console.Output;
        Assert.Contains("Cache miss", output);
        Assert.DoesNotContain("Download completed", output);
    }

    private (LogsCommand Command, TestConsole Console) CreateCommand(IEnumerable<string> lines)
    {
        var logPath = Path.Combine(_tempRoot, ".log");
        // Write each line as NDJSON (newline-delimited JSON)
        File.WriteAllLines(logPath, lines);

        var pathService = CreatePathService(logPath);
        var console = new TestConsole();
        var logger = NullLogger<LogsCommand>.Instance;

        var command = new LogsCommand(pathService, console, logger);
        return (command, console);
    }

    private static IPathService CreatePathService(string logPath)
    {
        var rootPath = Path.GetDirectoryName(logPath)!;
        var binPath = Path.Combine(rootPath, "bin");

        var mock = new Mock<IPathService>();
        mock.SetupGet(x => x.LogPath).Returns(logPath);
        mock.SetupGet(x => x.RootPath).Returns(rootPath);
        mock.SetupGet(x => x.ConfigPath).Returns(Path.Combine(rootPath, "fgvm.ini"));
        mock.SetupGet(x => x.ReleasesPath).Returns(Path.Combine(rootPath, "releases.json"));
        mock.SetupGet(x => x.BinPath).Returns(binPath);
        mock.SetupGet(x => x.SymlinkPath).Returns(Path.Combine(binPath, "godot"));
        mock.SetupGet(x => x.MacAppSymlinkPath).Returns(Path.Combine(binPath, "Godot.app"));
        return mock.Object;
    }
}
