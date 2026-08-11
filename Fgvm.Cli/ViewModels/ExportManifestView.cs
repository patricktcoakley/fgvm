using System.Text.Json.Serialization;

namespace Fgvm.Cli.ViewModels;

internal readonly record struct ExportTargetView(
    [property: JsonPropertyName("preset")]
    string Preset,
    [property: JsonPropertyName("platform")]
    string Platform,
    [property: JsonPropertyName("mode")]
    string Mode,
    [property: JsonPropertyName("kind")]
    string Kind,
    [property: JsonPropertyName("path")]
    string Path
);

internal readonly record struct ExportManifestView(
    [property: JsonPropertyName("manifestVersion")]
    int ManifestVersion,
    [property: JsonPropertyName("runId")]
    Guid RunId,
    [property: JsonPropertyName("godot")]
    string Godot,
    [property: JsonPropertyName("targets")]
    IReadOnlyList<ExportTargetView> Targets
) : IJsonView<ExportManifestView>
{
    internal const int CurrentVersion = 1;
}
