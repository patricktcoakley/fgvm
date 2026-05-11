namespace Fgvm.Godot;

/// <summary>
///     Artifact selected for installation. Sha512 is null when upstream metadata does not provide a checksum.
/// </summary>
public sealed record ReleaseArtifact(string FileName, string? Sha512);
