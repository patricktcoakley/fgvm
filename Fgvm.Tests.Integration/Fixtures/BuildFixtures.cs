#:property TargetFramework=net10.0
#:property LangVersion=14

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var context = BuildContext.Create(BuildOptions.Parse(args));
var manifestPath = await new FixtureBuilder(context).BuildAsync();
Console.WriteLine(manifestPath);

internal sealed class FixtureBuilder(BuildContext context)
{
    public async Task<string> BuildAsync()
    {
        var recipe = await ReadRecipeAsync(context.RecipePath);
        var platform = recipe.GetPlatform(context.Platform.Name);
        var outputs = FixtureOutputPaths.Create(context.OutputRoot, platform.Platform);

        outputs.Recreate();

        var publishedApps = await PublishMockAppsAsync(platform, outputs.PublishRoot);
        var manifest = await CreateManifestAsync(recipe, platform, outputs, publishedApps);

        await WriteManifestAsync(outputs.ManifestPath, manifest);
        return outputs.ManifestPath;
    }

    private static async Task<FixtureRecipe> ReadRecipeAsync(string recipePath)
    {
        await using var stream = File.OpenRead(recipePath);
        return await JsonSerializer.DeserializeAsync(stream, FixtureBuilderJsonContext.Default.FixtureRecipe)
               ?? throw new InvalidOperationException($"Fixture recipe '{recipePath}' is empty or invalid.");
    }

    private async Task<IReadOnlyDictionary<string, PublishedMockApp>> PublishMockAppsAsync(FixturePlatform platform, string publishRoot)
    {
        var publishedApps = new Dictionary<string, PublishedMockApp>(StringComparer.OrdinalIgnoreCase);
        var buildPlatforms = platform.Fixtures
            .Select(fixture => fixture.Platform)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var buildPlatformName in buildPlatforms)
        {
            var buildPlatform = BuildPlatform.Parse(buildPlatformName);
            var publishDirectory = Path.Combine(publishRoot, buildPlatform.Name);

            await DotNet.PublishFileAppAsync(
                context.MockSourcePath,
                buildPlatform.RuntimeIdentifier,
                publishDirectory);

            publishedApps[buildPlatform.Name] = PublishedMockApp.FromDirectory(publishDirectory, buildPlatform);
        }

        return publishedApps;
    }

    private static async Task<GeneratedFixtureManifest> CreateManifestAsync(FixtureRecipe recipe,
        FixturePlatform platform,
        FixtureOutputPaths outputs,
        IReadOnlyDictionary<string, PublishedMockApp> publishedApps
    )
    {
        var manifest = GeneratedFixtureManifest.Create(recipe, platform);

        foreach (var release in recipe.Releases)
        {
            foreach (var fixture in platform.Fixtures)
            {
                var zipName = fixture.ExpandZipName(release.Name);
                var executableName = fixture.ExpandExecutableName(release.Name);
                var zipPath = Path.Combine(outputs.ZipsRoot, zipName);

                ZipFixture.Create(
                    publishedApps[fixture.Platform],
                    zipPath,
                    platform.OS,
                    executableName);

                manifest.Artifacts.Add(new GeneratedFixtureArtifact
                {
                    ReleaseName = release.Name,
                    Runtime = fixture.Runtime,
                    Target = fixture.Target,
                    FileName = zipName,
                    ZipPath = PortablePath.FromRelative(outputs.PlatformRoot, zipPath),
                    Sha512 = await Checksum.Sha512Async(zipPath)
                });
            }
        }

        return manifest;
    }

    private static async Task WriteManifestAsync(string manifestPath, GeneratedFixtureManifest manifest)
    {
        await using var stream = File.Create(manifestPath);
        await JsonSerializer.SerializeAsync(stream, manifest, FixtureBuilderJsonContext.Default.GeneratedFixtureManifest);
    }
}

internal sealed record BuildContext(string RepoRoot, string RecipePath, string OutputRoot, BuildPlatform Platform)
{
    public string MockSourcePath => Path.Combine(RepoRoot, "Fgvm.Tests.Integration", "Fixtures", "MockGodot.cs");

    public static BuildContext Create(BuildOptions options)
    {
        var repoRoot = options.RepoRoot ?? RepositoryPaths.FindRoot();
        var recipePath = options.RecipePath ?? Path.Combine(repoRoot, "Fgvm.Tests.Integration", "Fixtures", "fixture-recipe.json");
        var outputRoot = options.OutputRoot ?? Path.Combine(repoRoot, ".fgvm-integration-fixtures");
        var platform = options.Platform is null ? BuildPlatform.Current() : BuildPlatform.Parse(options.Platform);

        return new BuildContext(repoRoot, recipePath, outputRoot, platform);
    }
}

internal sealed record FixtureOutputPaths(string PlatformRoot, string ZipsRoot, string PublishRoot, string ManifestPath)
{
    public static FixtureOutputPaths Create(string outputRoot, string platform)
    {
        var platformRoot = Path.Combine(outputRoot, platform);
        return new FixtureOutputPaths(
            platformRoot,
            Path.Combine(platformRoot, "zips"),
            Path.Combine(platformRoot, "publish"),
            Path.Combine(platformRoot, "manifest.json"));
    }

    public void Recreate()
    {
        if (Directory.Exists(PlatformRoot))
        {
            Directory.Delete(PlatformRoot, true);
        }

        Directory.CreateDirectory(ZipsRoot);
        Directory.CreateDirectory(PublishRoot);
    }
}

internal sealed record PublishedMockApp(string DirectoryPath, string ExecutableFileName)
{
    private const string AppHostName = "MockGodot";
    private const string WindowsAppHostName = "MockGodot.exe";

    public static PublishedMockApp FromDirectory(string directoryPath, BuildPlatform platform)
    {
        var expectedName = platform.IsWindows ? WindowsAppHostName : AppHostName;
        if (File.Exists(Path.Combine(directoryPath, expectedName)))
        {
            return new PublishedMockApp(directoryPath, expectedName);
        }

        var executableName = Directory.EnumerateFiles(directoryPath)
                                 .Select(Path.GetFileName)
                                 .FirstOrDefault(IsAppHost)
                             ?? throw new InvalidOperationException($"Could not find published MockGodot apphost in {directoryPath}.");

        return new PublishedMockApp(directoryPath, executableName);
    }

    private static bool IsAppHost(string? fileName) =>
        string.Equals(fileName, AppHostName, StringComparison.Ordinal) ||
        string.Equals(fileName, WindowsAppHostName, StringComparison.OrdinalIgnoreCase);
}

internal static class ZipFixture
{
    private const string SourceBaseName = "MockGodot";

    public static void Create(PublishedMockApp app, string zipPath, string os, string executableName)
    {
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (IsMacOS(os))
        {
            CreateMacAppDirectories(zip, executableName);
        }

        foreach (var filePath in Directory.EnumerateFiles(app.DirectoryPath).Where(ShouldInclude))
        {
            AddPublishedFile(zip, filePath, app.ExecutableFileName, os, executableName);
        }
    }

    private static void CreateMacAppDirectories(ZipArchive zip, string appName)
    {
        zip.CreateEntry(PortablePath.ToZip($"{appName}/"));
        zip.CreateEntry(PortablePath.ToZip(Path.Combine(appName, "Contents") + Path.DirectorySeparatorChar));
        zip.CreateEntry(PortablePath.ToZip(Path.Combine(appName, "Contents", "MacOS") + Path.DirectorySeparatorChar));
    }

    private static bool ShouldInclude(string filePath) =>
        !string.Equals(Path.GetExtension(filePath), ".pdb", StringComparison.OrdinalIgnoreCase);

    private static void AddPublishedFile(ZipArchive zip, string filePath, string sourceExecutableName, string os, string executableName)
    {
        var fileName = Path.GetFileName(filePath);
        var entryName = GetEntryName(os, fileName, sourceExecutableName, executableName);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);

        if (!IsWindows(os))
        {
            entry.ExternalAttributes = UnixExternalAttributes(IsExecutableEntry(os, entryName));
        }

        using var source = File.OpenRead(filePath);
        using var destination = entry.Open();
        source.CopyTo(destination);
    }

    private static string GetEntryName(string os, string fileName, string sourceExecutableName, string executableName)
    {
        if (!IsMacOS(os))
        {
            return PortablePath.ToZip(RenamePublishedFile(fileName, sourceExecutableName, executableName));
        }

        var macExecutableName = RenamePublishedFile(fileName, sourceExecutableName, "Godot");
        return PortablePath.ToZip(Path.Combine(executableName, "Contents", "MacOS", macExecutableName));
    }

    private static string RenamePublishedFile(string fileName, string sourceExecutableName, string executableName)
    {
        if (string.Equals(fileName, sourceExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return executableName;
        }

        if (!fileName.StartsWith($"{SourceBaseName}.", StringComparison.OrdinalIgnoreCase))
        {
            return fileName;
        }

        var sidecarBaseName = executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executableName[..^".exe".Length]
            : executableName;

        return sidecarBaseName + fileName[SourceBaseName.Length..];
    }

    private static bool IsExecutableEntry(string os, string entryName) =>
        IsMacOS(os)
            ? entryName.EndsWith("/Contents/MacOS/Godot", StringComparison.Ordinal)
            : !entryName.Contains('/', StringComparison.Ordinal) &&
              !entryName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
              !entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static int UnixExternalAttributes(bool executable)
    {
        var mode = executable
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
              UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
              UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite |
              UnixFileMode.GroupRead |
              UnixFileMode.OtherRead;

        return (int)mode << 16;
    }

    private static bool IsMacOS(string os) =>
        string.Equals(os, "macos", StringComparison.OrdinalIgnoreCase);

    private static bool IsWindows(string os) =>
        string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase);
}

internal readonly record struct BuildPlatform(string Name)
{
    public string RuntimeIdentifier => Name.ToLowerInvariant() switch
    {
        "linux-x64" => "linux-x64",
        "linux-arm64" => "linux-arm64",
        "macos-x64" => "osx-x64",
        "macos-arm64" => "osx-arm64",
        "windows-x64" => "win-x64",
        "windows-arm64" => "win-arm64",
        _ => throw new InvalidOperationException($"Unsupported fixture build platform '{Name}'.")
    };

    public bool IsWindows => Name.StartsWith("windows-", StringComparison.OrdinalIgnoreCase);

    public static BuildPlatform Current()
    {
        var os = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsMacOS()
                ? "macos"
                : OperatingSystem.IsLinux()
                    ? "linux"
                    : throw new PlatformNotSupportedException("Only Windows, macOS, and Linux are supported for fixtures.");

        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {RuntimeInformation.ProcessArchitecture}")
        };

        return new BuildPlatform($"{os}-{arch}");
    }

    public static BuildPlatform Parse(string name)
    {
        var platform = new BuildPlatform(name);
        _ = platform.RuntimeIdentifier;
        return platform;
    }
}

internal static class DotNet
{
    public static Task PublishFileAppAsync(string sourcePath, string rid, string outputPath) =>
        CommandRunner.RunAsync("dotnet",
        [
            "publish",
            sourcePath,
            "--nologo",
            "-c",
            "Release",
            "-r",
            rid,
            "--self-contained",
            "false",
            "-p:PublishSingleFile=true",
            "-p:UseAppHost=true",
            "-p:PublishAot=false",
            "-p:PublishTrimmed=false",
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "-o",
            outputPath
        ]);
}

internal static class Checksum
{
    public static async Task<string> Sha512Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA512.HashDataAsync(stream);
        return Convert.ToHexStringLower(hash);
    }
}

internal static class PortablePath
{
    public static string FromRelative(string relativeTo, string path) =>
        ToZip(Path.GetRelativePath(relativeTo, path));

    public static string ToZip(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}

internal static class RepositoryPaths
{
    public static string FindRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Fgvm.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
    }
}

internal static class CommandRunner
{
    public static async Task RunAsync(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{fileName} {string.Join(' ', arguments)} failed with exit code {process.ExitCode}.{Environment.NewLine}{stdout}{Environment.NewLine}{stderr}");
    }
}

internal sealed record BuildOptions(string? RepoRoot, string? RecipePath, string? OutputRoot, string? Platform)
{
    public static BuildOptions Parse(string[] args)
    {
        string? repoRoot = null;
        string? recipePath = null;
        string? outputRoot = null;
        string? platform = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo-root":
                    repoRoot = RequireValue(args, ref i);
                    break;
                case "--recipe":
                    recipePath = RequireValue(args, ref i);
                    break;
                case "--output":
                    outputRoot = RequireValue(args, ref i);
                    break;
                case "--platform":
                    platform = RequireValue(args, ref i);
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'.");
            }
        }

        return new BuildOptions(repoRoot, recipePath, outputRoot, platform);
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for '{args[index]}'.");
        }

        return args[++index];
    }
}

internal sealed class FixtureRecipe
{
    [JsonPropertyName("mockVersion")]
    public required string MockVersion { get; init; }

    [JsonPropertyName("releases")]
    public List<GeneratedFixtureRelease> Releases { get; init; } = [];

    [JsonPropertyName("platforms")]
    public List<FixturePlatform> Platforms { get; init; } = [];

    public FixturePlatform GetPlatform(string platform) =>
        Platforms.FirstOrDefault(x => string.Equals(x.Platform, platform, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"No fixture recipe exists for platform '{platform}'.");
}

internal sealed class FixturePlatform
{
    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("os")]
    public required string OS { get; init; }

    [JsonPropertyName("arch")]
    public required string Arch { get; init; }

    [JsonPropertyName("fixtures")]
    public List<FixtureArtifactRecipe> Fixtures { get; init; } = [];
}

internal sealed class FixtureArtifactRecipe
{
    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("runtime")]
    public required string Runtime { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("executableName")]
    public required string ExecutableName { get; init; }

    [JsonPropertyName("zipName")]
    public required string ZipName { get; init; }

    public string ExpandExecutableName(string releaseName) => Expand(ExecutableName, releaseName);

    public string ExpandZipName(string releaseName) => Expand(ZipName, releaseName);

    private static string Expand(string value, string releaseName) =>
        value.Replace("{release}", releaseName, StringComparison.Ordinal);
}

internal sealed class GeneratedFixtureManifest
{
    [JsonPropertyName("mockVersion")]
    public required string MockVersion { get; init; }

    [JsonPropertyName("platform")]
    public required string Platform { get; init; }

    [JsonPropertyName("releases")]
    public List<GeneratedFixtureRelease> Releases { get; init; } = [];

    [JsonPropertyName("artifacts")]
    public List<GeneratedFixtureArtifact> Artifacts { get; init; } = [];

    public static GeneratedFixtureManifest Create(FixtureRecipe recipe, FixturePlatform platform) =>
        new()
        {
            MockVersion = recipe.MockVersion,
            Platform = platform.Platform,
            Releases = [.. recipe.Releases]
        };
}

internal sealed class GeneratedFixtureRelease
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("releaseDate")]
    public long? ReleaseDate { get; init; }

    [JsonPropertyName("gitReference")]
    public string? GitReference { get; init; }
}

internal sealed class GeneratedFixtureArtifact
{
    [JsonPropertyName("releaseName")]
    public required string ReleaseName { get; init; }

    [JsonPropertyName("runtime")]
    public required string Runtime { get; init; }

    [JsonPropertyName("target")]
    public required string Target { get; init; }

    [JsonPropertyName("fileName")]
    public required string FileName { get; init; }

    [JsonPropertyName("zipPath")]
    public required string ZipPath { get; init; }

    [JsonPropertyName("sha512")]
    public required string Sha512 { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(FixtureRecipe))]
[JsonSerializable(typeof(GeneratedFixtureManifest))]
internal partial class FixtureBuilderJsonContext : JsonSerializerContext;
