using System.Text;
using Fgvm.Environment;
using Fgvm.Types;

namespace Fgvm.Godot.Export;

internal enum ExportArtifactKind
{
    Directory,
    File
}

internal enum GodotExportKind
{
    Release,
    Pack
}

internal enum ExportDestinationKind
{
    File,
    Directory,
    Either
}

internal sealed record PlannedExportTarget(
    string Preset,
    string Platform,
    string Destination,
    string ArtifactPath,
    ExportArtifactKind Kind,
    ExportDestinationKind DestinationKind,
    GodotExportKind ExportKind
);

internal abstract record ExportPlanError
{
    internal string Message => this switch
    {
        NoPresets => "No export presets are configured, so there is nothing to export.",
        IncompletePreset(var index, var missingField) =>
            $"Export preset {index} has no {missingField}. Configure it before exporting.",
        MissingExportPath(var preset, var platform) =>
            $"Export preset '{preset}' targets '{platform}', whose default output file name is unknown. " +
            "Configure export_path, or use --output only with a built-in Godot platform.",
        InvalidDestination(var preset, var reason) =>
            $"Export preset '{preset}' has an invalid destination: {reason}",
        DuplicateDestination(var first, var second, var destination) =>
            $"Export presets '{first}' and '{second}' both write to '{destination}'. Give one of them its own export path.",
        DuplicatePresetName(var name) =>
            $"More than one export preset is named '{name}'. Preset names must be unique because Godot exports by name.",
        _ => throw new InvalidOperationException("Unexpected export plan error type.")
    };

    internal sealed record NoPresets : ExportPlanError;

    internal sealed record IncompletePreset(int Index, string Field) : ExportPlanError;

    internal sealed record MissingExportPath(string Preset, string Platform) : ExportPlanError;

    internal sealed record InvalidDestination(string Preset, string Reason) : ExportPlanError;

    internal sealed record DuplicateDestination(string First, string Second, string Destination) : ExportPlanError;

    internal sealed record DuplicatePresetName(string Name) : ExportPlanError;
}

internal static class ExportPlanner
{
    private const string FallbackDirectory = "dist";

    internal static Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError> Plan(IReadOnlyList<ExportPreset> presets,
        string projectRoot,
        OS hostOS,
        string? outputRoot = null,
        bool archive = false
    )
    {
        if (presets is [])
        {
            return Failed(new ExportPlanError.NoPresets());
        }

        var targets = new List<PlannedExportTarget>(presets.Count);
        var comparison = hostOS == OS.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var claimedOutputs = new List<(string Path, string Preset)>(presets.Count);
        var claimedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var preset in presets)
        {
            if (string.IsNullOrWhiteSpace(preset.Name))
            {
                return Failed(new ExportPlanError.IncompletePreset(preset.Index, "name"));
            }

            if (string.IsNullOrWhiteSpace(preset.Platform))
            {
                return Failed(new ExportPlanError.IncompletePreset(preset.Index, "platform"));
            }

            if (!claimedNames.Add(preset.Name))
            {
                return Failed(new ExportPlanError.DuplicatePresetName(preset.Name));
            }

            PlannedExportTarget target;
            try
            {
                var targetResult = CreateTarget(preset, projectRoot, outputRoot, archive);
                if (targetResult is Result<PlannedExportTarget, ExportPlanError>.Failure(var error))
                {
                    return Failed(error);
                }

                target = targetResult is Result<PlannedExportTarget, ExportPlanError>.Success(var planned)
                    ? planned
                    : throw new InvalidOperationException("Unexpected export target result type.");
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Failed(new ExportPlanError.InvalidDestination(preset.Name!, exception.Message));
            }

            if (!TryClaim(claimedOutputs, target.ArtifactPath, target.Preset, comparison, out var conflict))
            {
                return Failed(new ExportPlanError.DuplicateDestination(
                    conflict!, target.Preset, target.ArtifactPath));
            }

            targets.Add(target);
        }

        return new Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Success(targets);
    }

    private static Result<PlannedExportTarget, ExportPlanError> CreateTarget(ExportPreset preset,
        string projectRoot,
        string? outputRoot,
        bool archive
    )
    {
        var name = preset.Name!;
        var platform = preset.Platform!;
        var slug = Slug(name);
        var isolated = outputRoot is not null || string.IsNullOrWhiteSpace(preset.ExportPath);

        if (archive)
        {
            var archivePath = Path.GetFullPath(outputRoot switch
            {
                { } root => Path.Combine(root, slug, slug + ".zip"),
                null when preset.ExportPath is { } exportPath && !string.IsNullOrWhiteSpace(exportPath) =>
                    Path.ChangeExtension(Path.Combine(projectRoot, exportPath), ".zip"),
                _ => Path.Combine(projectRoot, FallbackDirectory, slug, slug + ".zip")
            });

            return new PlannedExportTarget(
                name,
                platform,
                archivePath,
                archivePath,
                ExportArtifactKind.File,
                ExportDestinationKind.File,
                GodotExportKind.Pack);
        }

        var fileName = isolated ? ExportPlatform.DefaultFileName(platform, slug) : null;

        if (isolated && fileName is null)
        {
            return new Result<PlannedExportTarget, ExportPlanError>.Failure(
                new ExportPlanError.MissingExportPath(name, platform));
        }

        var destination = Path.GetFullPath(outputRoot switch
        {
            { } root => Path.Combine(root, slug, fileName!),
            null when preset.ExportPath is { } exportPath && !string.IsNullOrWhiteSpace(exportPath) =>
                Path.Combine(projectRoot, exportPath),
            _ => Path.Combine(projectRoot, FallbackDirectory, slug, fileName!)
        });

        var shape = ExportPlatform.ArtifactShape(platform, destination);
        var containingDirectory = Path.GetDirectoryName(destination) ?? destination;
        var (artifactPath, kind) = shape switch
        {
            ExportPlatformShape.File =>
                (destination, ExportArtifactKind.File),
            ExportPlatformShape.DestinationDirectory =>
                (destination, ExportArtifactKind.Directory),
            ExportPlatformShape.ContainingDirectory =>
                (containingDirectory, ExportArtifactKind.Directory),
            _ => throw new InvalidOperationException("Unexpected export platform shape.")
        };

        return new PlannedExportTarget(
            name,
            platform,
            destination,
            artifactPath,
            kind,
            ExportPlatform.ExpectedDestinationKind(platform, destination),
            GodotExportKind.Release);
    }

    private static bool TryClaim(List<(string Path, string Preset)> claimed,
        string path,
        string preset,
        StringComparison comparison,
        out string? conflict
    )
    {
        foreach (var existing in claimed)
        {
            if (IsSameOrNested(existing.Path, path, comparison) ||
                IsSameOrNested(path, existing.Path, comparison))
            {
                conflict = existing.Preset;
                return false;
            }
        }

        claimed.Add((path, preset));
        conflict = null;
        return true;
    }

    private static bool IsSameOrNested(string parent, string candidate, StringComparison comparison) =>
        string.Equals(parent, candidate, comparison) ||
        candidate.StartsWith(parent, comparison) &&
        (Path.EndsInDirectorySeparator(parent) ||
         candidate[parent.Length] == Path.DirectorySeparatorChar ||
         candidate[parent.Length] == Path.AltDirectorySeparatorChar);

    private static Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError> Failed(ExportPlanError error) =>
        new Result<IReadOnlyList<PlannedExportTarget>, ExportPlanError>.Failure(error);

    private static string Slug(string name)
    {
        var slug = new StringBuilder(name.Length);
        var pendingSeparator = false;

        foreach (var character in name)
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingSeparator && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(char.ToLowerInvariant(character));
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return slug.Length > 0 ? slug.ToString() : "target";
    }
}
