using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fgvm.Cli.Error;
using Fgvm.Cli.ViewModels;
using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Spectre.Console;


namespace Fgvm.Cli.Command;

public sealed class LogsCommand(
    IPathService pathService,
    IHostSystem hostSystem,
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
    /// <exception cref="FileNotFoundException">Thrown when the configured log file does not exist.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the requested log level filter is invalid.</exception>
    /// <exception cref="OperationCanceledException">Thrown when log reading is canceled.</exception>
    public async Task Logs(bool json = false, string level = "", string message = "", CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (hostSystem.FileExists(pathService.LogPath))
            {
                case Result<bool, FileOperationError>.Failure(var existsError):
                    throw new InvalidOperationException($"Unable to read log path `{pathService.LogPath}`: {existsError}");
                case Result<bool, FileOperationError>.Success { Value: false }:
                    throw new FileNotFoundException(Messages.LogPathNotFound(pathService.LogPath));
                case Result<bool, FileOperationError>.Success:
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            if (!string.IsNullOrEmpty(level) && !LogLevels.Any(x => x.StartsWith(level, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentOutOfRangeException(Messages.LogLevelOutOfRange(level));
            }

            Stream streamValue;
            switch (hostSystem.OpenRead(pathService.LogPath, FileShare.ReadWrite))
            {
                case Result<Stream, FileOperationError>.Failure(var readError):
                    throw new InvalidOperationException($"Unable to read log file `{pathService.LogPath}`: {readError}");
                case Result<Stream, FileOperationError>.Success(var openedStream):
                    streamValue = openedStream;
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Result type");
            }

            await using var stream = streamValue;

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

            if (json)
            {
                console.Profile.Out.Writer.WriteLine(entries.ToJson());
                return;
            }

            console.WriteLine(entries.ToSlog(malformed));
        }
        catch (TaskCanceledException)
        {
            logger.LogError("User cancelled reading the logs.");
            console.MarkupLine(Messages.UserCancelled("reading logs"));

            throw;
        }
        catch (Exception e)
        {
            logger.LogError(e, "Error reading the logs: {Message}", e.Message);
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
    string Category
) : IJsonView<LogEntryView>
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
