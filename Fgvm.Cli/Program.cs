using System.Buffers;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Fgvm.Cli.Command;
using Fgvm.Cli.Filter;
using Fgvm.Cli.Http;
using Fgvm.Cli.Progress;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Error;
using Fgvm.Godot;
using Fgvm.Progress;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Serilog.Formatting;
using Spectre.Console;

namespace Fgvm.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var pathService = new PathService();
        var fixtureManifestPath = System.Environment.GetEnvironmentVariable("FGVM_INTEGRATION_FIXTURE_MANIFEST");
        var fixtureMode = !string.IsNullOrWhiteSpace(fixtureManifestPath);

        var services = new ServiceCollection();

        // Lazy logging - only opens file handle when first logger is created
        services.AddSingleton<Lazy<ILoggerFactory>>(_ => new Lazy<ILoggerFactory>(() =>
        {
            var serilogLogger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(new SlogFormatter(), pathService.LogPath)
                .CreateLogger();

            return LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Information);

                // Suppress automatic HTTP client logging - we'll add our own custom logging
                builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

                builder.AddSerilog(serilogLogger);
            });
        }));

        // Add logging infrastructure that uses the lazy factory
        services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<Lazy<ILoggerFactory>>().Value);
        services.AddLogging();

        // Register services
        services.AddSingleton<IPathService>(pathService);
        services.AddSingleton(_ => CreateSystemInfo(fixtureMode));
        services.AddSingleton<PlatformStringProvider>();
        services.AddSingleton<IGodotPathService, GodotPathService>();
        services.AddSingleton<IHostSystem, HostSystem>();

        if (fixtureMode)
        {
            services.AddSingleton<IDownloadClient>(sp =>
                new FixtureDownloadClient(fixtureManifestPath!, sp.GetRequiredService<ILogger<FixtureDownloadClient>>()));
        }
        else
        {
            // Register HTTP clients
            services.AddHttpClient<IDownloadClient, DownloadClient>("godot-builds")
                .ConfigureHttpClient((_, client) =>
                {
                    var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
                    client.DefaultRequestHeaders.UserAgent.Clear();
                    client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("fgvm", version));
                    client.Timeout = TimeSpan.FromSeconds(30);
                })
                .AddHttpMessageHandler(() => new ExponentialBackoffHandler(TimeSpan.FromSeconds(2), 3));
        }

        // Register core services
        services.AddSingleton<IReleaseCatalog, ReleaseCatalog>();
        services.AddSingleton<IReleaseManager, ReleaseManager>();
        services.AddSingleton<IInstallationRegistry, InstallationRegistry>();
        services.AddSingleton<ITemplateRegistry, TemplateRegistry>();
        services.AddSingleton<IInstallationService, InstallationService>();
        services.AddSingleton<ITemplateInstallationService, TemplateInstallationService>();
        services.AddSingleton<IProjectManager, ProjectManager>();
        services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
        services.AddSingleton<ITemplateOrchestrator, TemplateOrchestrator>();
        services.AddSingleton<IVersionManagementService, VersionManagementService>();
        services.AddSingleton<IGodotArgumentService, GodotArgumentService>();
        services.AddSingleton<IGodotLauncher, GodotLauncher>();
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);

        // Progress handling
        services.AddSingleton<IProgressHandler<InstallationStage>, SpectreProgressHandler<InstallationStage>>();
        services.AddSingleton<IProgressHandler<TemplateInstallationStage>, SpectreProgressHandler<TemplateInstallationStage>>();

        using var serviceProvider = services.BuildServiceProvider();

        if (serviceProvider.GetRequiredService<IHostSystem>().CreateDirectory(pathService.BinPath) is
            Result<Unit, FileOperationError>.Failure(var binError))
        {
            throw new InvalidOperationException($"Unable to create fgvm bin directory: {binError}");
        }

        ConsoleApp.ServiceProvider = serviceProvider;

        var app = ConsoleApp.Create();

        app.Add<GodotCommand>();
        app.Add<SetCommand>();
        app.Add<WhichCommand>();
        app.Add<InstallCommand>();
        app.Add<ListCommand>();
        app.Add<RemoveCommand>();
        app.Add<LogsCommand>();
        app.Add<SearchCommand>();
        app.Add<LocalCommand>();
        app.Add<TemplateHelpCommand>();
        app.Add<TemplateCommand>("template");
        app.Add<TemplateCommand>("t");

        app.UseFilter<ExitCodeFilter>();

        // ConsoleAppFramework does not dispatch custom help for hidden grouped subcommands.
        if (TemplateHelpCommand.TryWriteHelp(args, System.Console.Out))
        {
            return 0;
        }

        app.Run(args);

        return 0;
    }

    private static SystemInfo CreateSystemInfo(bool fixtureMode)
    {
        var systemInfo = new SystemInfo();
        if (!fixtureMode || System.Environment.GetEnvironmentVariable("FGVM_INTEGRATION_ARCH_OVERRIDE") is not { Length: > 0 } archOverride)
        {
            return systemInfo;
        }

        var architecture = archOverride.ToLowerInvariant() switch
        {
            "x64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            _ => throw new ConfigurationException("FGVM_INTEGRATION_ARCH_OVERRIDE must be either 'x64' or 'arm64'.")
        };

        return new SystemInfo(systemInfo.CurrentOS, architecture);
    }
}

public class SlogFormatter : ITextFormatter
{
    public void Format(LogEvent logEvent, TextWriter output)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteString("Timestamp", logEvent.Timestamp.UtcDateTime.ToString("O"));
        writer.WriteString("LogLevel", logEvent.Level.ToString());
        writer.WriteString("Category",
            logEvent.Properties.TryGetValue("SourceContext", out var sourceContext)
                ? sourceContext.ToString().Trim('"')
                : "");
        writer.WriteString("Message", logEvent.RenderMessage());
        writer.WriteEndObject();
        writer.Flush();

        output.Write(Encoding.UTF8.GetString(buffer.WrittenSpan));
        output.WriteLine();
    }
}
