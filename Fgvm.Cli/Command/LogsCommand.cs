using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZLogger;

namespace Fgvm.Cli.Command;

public sealed class LogsCommand(
    IPathService pathService,
    IAnsiConsole console,
    ILogger<LogsCommand> logger
)
{
    private static readonly string[] LogLevels = ["DEFAULT", "DEBUG", "INFORMATION", "WARNING", "ERROR", "CRITICAL"];

    /// <summary>
    ///     View application logs.
    /// </summary>
    /// <param name="json">Output logs to JSON.</param>
    /// <param name="level">-l, Level to filter by.</param>
    /// <param name="message">-m, Message text to filter by.</param>
    /// <param name="cancellationToken"></param>
    public async Task Logs(bool json = false, string level = "", string message = "", CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.Exists(pathService.LogPath))
            {
                throw new FileNotFoundException(Messages.LogPathNotFound(pathService.LogPath));
            }

            if (!string.IsNullOrEmpty(level) && !LogLevels.Any(x => x.StartsWith(level, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentOutOfRangeException(Messages.LogLevelOutOfRange(level));
            }


            await using var stream = new FileStream(
                pathService.LogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);

            using var reader = new StreamReader(stream, Encoding.UTF8);

            var entries = new List<LogEntryView>();
            var malformed = new List<string>();

            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize(line, JsonViewSerializerContext.Default.LogEntryView);

                    // Apply filters
                    var levelMatch = string.IsNullOrEmpty(level) ||
                                     entry.Level.Contains(level, StringComparison.OrdinalIgnoreCase);

                    var messageMatch = string.IsNullOrEmpty(message) ||
                                       entry.Message.Contains(message, StringComparison.OrdinalIgnoreCase);

                    if (levelMatch && messageMatch)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    malformed.Add(line);
                }
            }

            console.WriteLine(json ? entries.ToJson() : entries.ToSlog(malformed));
        }
        catch (TaskCanceledException)
        {
            logger.ZLogError($"User cancelled reading the logs.");
            console.MarkupLine(Messages.UserCancelled("reading logs"));

            throw;
        }
        catch (Exception e)
        {
            logger.ZLogError($"Error reading the logs: {e.Message}");
            console.MarkupLine(
                Messages.SomethingWentWrong("when trying to read the logs", pathService)
            );

            throw;
        }
    }
}

public readonly record struct LogEntryView(
    [property: JsonPropertyName("Timestamp")]
    DateTime Timestamp,
    [property: JsonPropertyName("LogLevel")]
    string Level,
    [property: JsonPropertyName("Message")]
    string Message,
    [property: JsonPropertyName("Category")]
    string Category) : IJsonView<LogEntryView>
{
    public override string ToString() =>
        $"Timestamp: {Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}, LogLevel: {Level}, Message: {Message}, Category: {Category}";
}

public static class LogEntryViewExtensions
{
    extension(IReadOnlyList<LogEntryView> entries)
    {
        public string ToJson() =>
            JsonViewExtensions.ToJson(entries);

        public string ToSlog(IReadOnlyList<string> malformed)
        {
            var builder = new StringBuilder();

            if (entries.Count > 0)
            {
                foreach (var entry in entries)
                {
                    builder.AppendLine(entry.ToString());
                }
            }

            if (malformed.Count > 0)
            {
                builder.AppendLine($"Skipped {malformed.Count} malformed log entries.");
            }

            if (entries.Count == 0 && malformed.Count == 0)
            {
                builder.AppendLine("No log entries found.");
            }

            return builder.ToString().TrimEnd();
        }
    }
}
