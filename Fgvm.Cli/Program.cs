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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Net.Http.Headers;
using ZLogger;

namespace Fgvm.Cli;

public class Program
{
    public static int Main(string[] args)
    {
        var pathService = new PathService();

        var services = new ServiceCollection();

        // Lazy logging - only opens file handle when first logger is created
        services.AddSingleton<Lazy<ILoggerFactory>>(_ => new Lazy<ILoggerFactory>(() =>
        {
            return LoggerFactory.Create(builder =>
            {
                builder.ClearProviders();
                builder.SetMinimumLevel(LogLevel.Information);

                // Suppress automatic HTTP client logging - we'll add our own custom logging
                builder.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

                builder.AddZLoggerFile(pathService.LogPath,
                    opts => { opts.UseJsonFormatter(); });
            });
        }));

        // Add logging infrastructure that uses the lazy factory
        services.AddSingleton<ILoggerFactory>(sp => sp.GetRequiredService<Lazy<ILoggerFactory>>().Value);
        services.AddLogging();

        // Register services
        services.AddSingleton<IPathService>(pathService);
        services.AddSingleton<SystemInfo>();
        services.AddSingleton<PlatformStringProvider>();
        services.AddSingleton<IHostSystem, HostSystem>();

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

        // Register core services
        services.AddSingleton<IReleaseCatalog, ReleaseCatalog>();
        services.AddSingleton<IReleaseManager, ReleaseManager>();
        services.AddSingleton<IInstallationRegistry, InstallationRegistry>();
        services.AddSingleton<IInstallationService, InstallationService>();
        services.AddSingleton<IProjectManager, ProjectManager>();
        services.AddSingleton<IInstallationOrchestrator, InstallationOrchestrator>();
        services.AddSingleton<IVersionManagementService, VersionManagementService>();
        services.AddSingleton<IGodotArgumentService, GodotArgumentService>();
        services.AddSingleton<IAnsiConsole>(_ => AnsiConsole.Console);

        // Progress handling
        services.AddSingleton<IProgressHandler<InstallationStage>, SpectreProgressHandler<InstallationStage>>();

        // Lazy configuration - only load and validate when first accessed
        services.AddSingleton<Lazy<IConfiguration>>(sp => new Lazy<IConfiguration>(() =>
        {
            var hostSystem = sp.GetRequiredService<IHostSystem>();

            // Ensure config file exists before loading
            var configExists = hostSystem.FileExists(pathService.ConfigPath);
            if (configExists is Result<bool, FileOperationError>.Failure(var existsError))
            {
                throw new ConfigurationException($"Unable to read configuration path: {existsError}");
            }

            if (configExists is Result<bool, FileOperationError>.Success { Value: false })
            {
                if (Path.GetDirectoryName(pathService.ConfigPath) is { } directory &&
                    hostSystem.CreateDirectory(directory) is Result<Unit, FileOperationError>.Failure(var createDirectoryError))
                {
                    throw new ConfigurationException($"Unable to create configuration directory: {createDirectoryError}");
                }

                if (hostSystem.WriteAllText(pathService.ConfigPath, "# FGVM Configuration File\n") is Result<Unit, FileOperationError>.Failure(var writeError))
                {
                    throw new ConfigurationException($"Unable to create configuration file: {writeError}");
                }
            }

            var configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddIniFile(pathService.ConfigPath, false, true)
                .AddEnvironmentVariables(prefix: "FGVM_")
                .Build();

            if (Configuration.ValidateConfiguration(configuration) is Result<Unit, ConfigError>.Failure(var error))
            {
                throw new ConfigurationException(error switch
                {
                    ConfigError.InvalidGitHubTokenPrefix =>
                        "GitHub token should start with 'github_pat_', 'ghp_', 'gho_', 'ghu_', 'ghs_', or 'ghr_' prefix",
                    ConfigError.InvalidGitHubTokenLength =>
                        "GitHub token length does not match a supported token format",
                    ConfigError.InvalidGitHubTokenCharacters =>
                        "GitHub token contains invalid characters",
                    _ => "Invalid configuration"
                });
            }

            return configuration;
        }));

        using var serviceProvider = services.BuildServiceProvider();

        if (serviceProvider.GetRequiredService<IHostSystem>().CreateDirectory(pathService.BinPath) is Result<Unit, FileOperationError>.Failure(var binError))
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

        app.UseFilter<ExitCodeFilter>();

        app.Run(args);

        return 0;
    }
}
