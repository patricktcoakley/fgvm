namespace Fgvm.Types;

/// <summary>
///     A top-level export preset declared in a Godot project's <c>export_presets.cfg</c> file.
/// </summary>
public sealed record ExportPreset(
    int Index,
    string? Name,
    string? Platform,
    string? ExportPath,
    bool? Runnable
)
{
    /// <summary>
    ///     Whether the preset contains the fields Godot needs to identify and write an export.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Name) &&
        !string.IsNullOrWhiteSpace(Platform) &&
        !string.IsNullOrWhiteSpace(ExportPath);
}
