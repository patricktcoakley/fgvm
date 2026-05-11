using System.Text.Json.Serialization;

namespace Fgvm.Godot;

public sealed class GodotReleaseManifest
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("release_date")]
    public long? ReleaseDate { get; init; }

    [JsonPropertyName("git_reference")]
    public string? GitReference { get; init; }

    [JsonPropertyName("files")]
    public List<GodotReleaseManifestFile> Files { get; init; } = [];
}

public sealed class GodotReleaseManifestFile
{
    [JsonPropertyName("filename")]
    public required string FileName { get; init; }

    [JsonPropertyName("checksum")]
    public required string Checksum { get; init; }
}
