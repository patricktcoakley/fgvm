using System.Text.Json;
using Fgvm.Cli.ViewModels;
using Fgvm.Godot;
using Fgvm.Services;
using Json.Schema;

namespace Fgvm.Tests.Contracts;

public sealed class PersistedJsonSchemaTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, JsonSchema> Schemas = LoadSchemas();

    private const string Sha512 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" +
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ExportManifestSerializer_ConformsToSchema()
    {
        var manifest = new ExportManifestView(
            ExportManifestView.CurrentVersion,
            Guid.Parse("7ee8ba02-6846-49e8-b9f5-c3468fc53ae6"),
            "4.6.2-stable-standard",
            [new ExportTargetView("Linux", "Linux", "release", "directory", "build/linux")]);

        AssertValid("fgvm-export-manifest-v1.schema.json", manifest.ToJson());
    }

    [Fact]
    public void InstallationRegistrySerializer_ConformsToSchema()
    {
        const string key = "4.6.2-stable-standard@linux-x86_64";
        var registry = new InstallationRegistryDocument
        {
            Default = key,
            Installations =
            {
                [key] = new InstallationRegistryEntry
                {
                    Path = "installations/4.6.2-stable-standard/linux-x86_64",
                    InstalledAt = Timestamp,
                    LastLaunchedAt = null
                }
            }
        };
        var json = JsonSerializer.Serialize(
            registry,
            InstallationRegistryJsonContext.Default.InstallationRegistryDocument);

        AssertValid("fgvm-installation-registry-v1.schema.json", json);
    }

    [Fact]
    public void ReleaseCatalogSerializer_ConformsToSchema()
    {
        var artifact = new ReleaseCatalogArtifact
        {
            FileName = "Godot_v4.6.2-stable_linux.x86_64.zip",
            Sha512 = Sha512
        };
        var release = new ReleaseCatalogRelease
        {
            ReleaseDate = 1_786_444_800,
            GitReference = "4.6.2-stable",
            Targets =
            {
                ["linux-x86_64"] = new ReleaseCatalogTarget
                {
                    ["standard"] = artifact
                }
            },
            Files =
            {
                [artifact.FileName] = artifact
            }
        };
        var catalog = new ReleaseCatalogManifest
        {
            LastUpdated = Timestamp,
            Releases =
            {
                ["4.6.2"] = new ReleaseCatalogVersion
                {
                    ["stable"] = release
                }
            }
        };
        var json = JsonSerializer.Serialize(
            catalog,
            ReleaseCatalogJsonContext.Default.ReleaseCatalogManifest);

        AssertValid("fgvm-release-catalog-v1.schema.json", json);
    }

    [Theory]
    [InlineData("fgvm-export-manifest-v1.schema.json")]
    [InlineData("fgvm-installation-registry-v1.schema.json")]
    [InlineData("fgvm-release-catalog-v1.schema.json")]
    public void PersistedSchemas_RejectEmptyDocuments(string schemaFile)
    {
        Assert.False(Evaluate(schemaFile, "{}").IsValid);
    }

    private static void AssertValid(string schemaFile, string json) =>
        Assert.True(Evaluate(schemaFile, json).IsValid, $"{schemaFile} rejected:\n{json}");

    private static EvaluationResults Evaluate(string schemaFile, string json)
    {
        using var instance = JsonDocument.Parse(json);
        return Schemas[schemaFile].Evaluate(
            instance.RootElement,
            new EvaluationOptions { RequireFormatValidation = true });
    }

    private static IReadOnlyDictionary<string, JsonSchema> LoadSchemas()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "schemas");
        return Directory
            .EnumerateFiles(directory, "*.schema.json")
            .ToDictionary(
                path => Path.GetFileName(path),
                path => JsonSchema.FromFile(path),
                StringComparer.Ordinal);
    }
}
