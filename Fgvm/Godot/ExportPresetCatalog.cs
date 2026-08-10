using System.Globalization;
using System.Text;
using Fgvm.Environment;
using Fgvm.Types;

namespace Fgvm.Godot;

public interface IExportPresetCatalog
{
    /// <summary>
    ///     Reads the top-level export presets configured for a Godot project.
    /// </summary>
    /// <param name="projectDirectory">Project directory, or the current directory when omitted.</param>
    Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError> Read(string? projectDirectory = null);
}

public abstract record ExportPresetCatalogError
{
    public sealed record FileAccess(FileOperationError Error) : ExportPresetCatalogError;

    public sealed record InvalidConfiguration(string Path, int Line, string Message) : ExportPresetCatalogError;
}

/// <summary>
///     Reads the small, stable subset of Godot's export preset configuration needed by fgvm.
/// </summary>
public sealed class ExportPresetCatalog(IHostSystem hostSystem) : IExportPresetCatalog
{
    public const string FileName = "export_presets.cfg";

    /// <inheritdoc />
    public Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError> Read(string? projectDirectory = null)
    {
        projectDirectory ??= Directory.GetCurrentDirectory();
        var path = Path.Combine(projectDirectory, FileName);

        return hostSystem.ReadAllText(path) switch
        {
            Result<string, FileOperationError>.Success(var content) => Parse(content, path),
            Result<string, FileOperationError>.Failure(FileOperationError.NotFound) =>
                new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success([]),
            Result<string, FileOperationError>.Failure(var error) =>
                new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(
                    new ExportPresetCatalogError.FileAccess(error)),
            _ => throw new InvalidOperationException("Unexpected result type")
        };
    }

    internal static Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError> Parse(string content, string path)
    {
        var builders = new Dictionary<int, PresetBuilder>();
        PresetBuilder? currentPreset = null;

        foreach (var entry in new ConfigScanner(content))
        {
            switch (entry)
            {
                case ConfigEntry.MalformedSection(_, var line):
                    return Failed(path, line, "Malformed section header.");

                case ConfigEntry.Section(var name, var line):
                    switch (SelectPreset(builders, name, path, line))
                    {
                        case Result<PresetBuilder?, ExportPresetCatalogError>.Failure(var sectionError):
                            return Failed(sectionError);
                        case Result<PresetBuilder?, ExportPresetCatalogError>.Success(var selected):
                            currentPreset = selected;
                            break;
                    }

                    break;

                // Lines outside an active top-level preset are never fgvm's concern.
                case ConfigEntry.MalformedAssignment when currentPreset is null:
                    break;

                case ConfigEntry.MalformedAssignment(_, var line):
                    return Failed(path, line, "Expected an export preset key/value pair.");

                case ConfigEntry.Assignment(_, var key, var value, var line) when currentPreset is not null:
                    if (Apply(currentPreset, key, value, path, line) is
                        Result<Unit, ExportPresetCatalogError>.Failure(var assignmentError))
                    {
                        return Failed(assignmentError);
                    }

                    break;

                case ConfigEntry.Multiline(_, var key, var value, var line) when currentPreset is not null:
                    if (RejectSpanningValue(key, value, path, line) is
                        Result<Unit, ExportPresetCatalogError>.Failure(var multilineError))
                    {
                        return Failed(multilineError);
                    }

                    break;

                // A value still open at end of file is malformed no matter whose key it is. Ignoring
                // it would silently discard every line the runaway value swallowed.
                case ConfigEntry.UnterminatedValue(_, var key, var value, var line):
                    if (currentPreset is not null &&
                        RejectSpanningValue(key, value, path, line) is
                            Result<Unit, ExportPresetCatalogError>.Failure(var unterminatedError))
                    {
                        return Failed(unterminatedError);
                    }

                    return Failed(path, line, $"Unterminated value for '{key}'.");
            }
        }

        var activePresets = new List<ExportPreset>();
        for (var index = 0; builders.TryGetValue(index, out var builder); index++)
        {
            activePresets.Add(builder.Build());
        }

        return new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Success(activePresets);
    }

    /// <summary>
    ///     Activates the builder for a top-level <c>[preset.N]</c> section, and clears the active
    ///     preset for anything else, including <c>[preset.N.options]</c>.
    /// </summary>
    private static Result<PresetBuilder?, ExportPresetCatalogError> SelectPreset(Dictionary<int, PresetBuilder> builders,
        string section,
        string path,
        int line
    )
    {
        if (!section.StartsWith("preset.", StringComparison.Ordinal))
        {
            return new Result<PresetBuilder?, ExportPresetCatalogError>.Success(null);
        }

        var presetSection = section["preset.".Length..];
        var separator = presetSection.IndexOf('.');
        var indexText = separator >= 0 ? presetSection[..separator] : presetSection;
        if (!int.TryParse(indexText, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0)
        {
            return new Result<PresetBuilder?, ExportPresetCatalogError>.Failure(
                Invalid(path, line, $"Invalid export preset section '{section}'."));
        }

        if (separator >= 0)
        {
            return new Result<PresetBuilder?, ExportPresetCatalogError>.Success(null);
        }

        if (builders.ContainsKey(index))
        {
            return new Result<PresetBuilder?, ExportPresetCatalogError>.Failure(
                Invalid(path, line, $"Duplicate export preset index {index}."));
        }

        var builder = new PresetBuilder(index);
        builders.Add(index, builder);
        return new Result<PresetBuilder?, ExportPresetCatalogError>.Success(builder);
    }

    private static Result<Unit, ExportPresetCatalogError> Apply(PresetBuilder preset,
        string key,
        string value,
        string path,
        int line
    )
    {
        switch (key)
        {
            case "name":
                return AssignString(preset, key, value, path, line, static (builder, decoded) => builder.Name = decoded);
            case "platform":
                return AssignString(preset, key, value, path, line, static (builder, decoded) => builder.Platform = decoded);
            case "export_path":
                return AssignString(preset, key, value, path, line, static (builder, decoded) => builder.ExportPath = decoded);
            case "runnable":
                if (value is not ("true" or "false"))
                {
                    return new Result<Unit, ExportPresetCatalogError>.Failure(
                        Invalid(path, line, "Invalid runnable value; expected true or false."));
                }

                if (!preset.MarkAssigned(key))
                {
                    return new Result<Unit, ExportPresetCatalogError>.Failure(
                        Invalid(path, line, $"Duplicate {key} value."));
                }

                preset.Runnable = value == "true";
                return Unit.Value;
            default:
                // Ignore metadata fgvm does not consume.
                return Unit.Value;
        }
    }

    /// <summary>
    ///     Decodes a quoted value and assigns it once. Decoding is validated before the duplicate
    ///     check so a malformed repeat still reports why it is malformed.
    /// </summary>
    private static Result<Unit, ExportPresetCatalogError> AssignString(PresetBuilder preset,
        string key,
        string value,
        string path,
        int line,
        Action<PresetBuilder, string> assign
    )
    {
        if (!TryReadString(value, out var decoded, out var error))
        {
            return new Result<Unit, ExportPresetCatalogError>.Failure(
                Invalid(path, line, $"Invalid {key} value: {error}"));
        }

        if (!preset.MarkAssigned(key))
        {
            return new Result<Unit, ExportPresetCatalogError>.Failure(
                Invalid(path, line, $"Duplicate {key} value."));
        }

        assign(preset, decoded);
        return Unit.Value;
    }

    /// <summary>
    ///     Godot writes every value fgvm consumes on one line, so a value that spans lines is only
    ///     an error when fgvm actually reads that key.
    /// </summary>
    private static Result<Unit, ExportPresetCatalogError> RejectSpanningValue(string key,
        string value,
        string path,
        int line
    )
    {
        switch (key)
        {
            case "name" or "platform" or "export_path":
                // Diagnose from the retained first line without assigning anything: the value is
                // being rejected, so the builder must not be mutated on the way out.
                return new Result<Unit, ExportPresetCatalogError>.Failure(
                    TryReadString(value, out _, out var error)
                        ? Invalid(path, line, $"Invalid {key} value; it must not span lines.")
                        : Invalid(path, line, $"Invalid {key} value: {error}"));
            case "runnable":
                return new Result<Unit, ExportPresetCatalogError>.Failure(
                    Invalid(path, line, "Invalid runnable value; expected true or false."));
            default:
                return Unit.Value;
        }
    }

    private static ExportPresetCatalogError.InvalidConfiguration Invalid(string path, int line, string message) =>
        new(path, line, message);

    private static Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError> Failed(ExportPresetCatalogError error) =>
        new Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError>.Failure(error);

    private static Result<IReadOnlyList<ExportPreset>, ExportPresetCatalogError> Failed(string path,
        int line,
        string message
    ) =>
        Failed(Invalid(path, line, message));

    private static bool TryReadString(string value, out string result, out string error)
    {
        result = string.Empty;
        error = string.Empty;
        if (value.Length < 2 || value[0] != '"')
        {
            error = "expected a quoted string.";
            return false;
        }

        var decoded = new StringBuilder(value.Length - 2);
        var escaped = false;
        for (var i = 1; i < value.Length; i++)
        {
            var character = value[i];
            if (escaped)
            {
                switch (character)
                {
                    case '"':
                    case '\\':
                        decoded.Append(character);
                        break;
                    case 'n':
                        decoded.Append('\n');
                        break;
                    case 'r':
                        decoded.Append('\r');
                        break;
                    case 't':
                        decoded.Append('\t');
                        break;
                    case 'b':
                        decoded.Append('\b');
                        break;
                    case 'f':
                        decoded.Append('\f');
                        break;
                    default:
                        error = $"unsupported escape sequence \\{character}.";
                        return false;
                }

                escaped = false;
                continue;
            }

            if (character == '\\')
            {
                escaped = true;
                continue;
            }

            if (character == '"')
            {
                if (i != value.Length - 1)
                {
                    error = "unexpected content after the closing quote.";
                    return false;
                }

                result = decoded.ToString();
                return true;
            }

            decoded.Append(character);
        }

        error = escaped ? "unterminated escape sequence." : "missing closing quote.";
        return false;
    }

    private sealed class PresetBuilder(int index)
    {
        private readonly HashSet<string> _assigned = [];

        public int Index { get; } = index;
        public string? Name { get; set; }
        public string? Platform { get; set; }
        public string? ExportPath { get; set; }
        public bool? Runnable { get; set; }

        /// <summary>
        ///     Records the first assignment of a consumed key and reports false for any repeat. A
        ///     blank value is legitimate for <c>export_path</c>, so presence cannot stand in for this.
        /// </summary>
        public bool MarkAssigned(string key) => _assigned.Add(key);

        public ExportPreset Build() => new(Index, Name, Platform, ExportPath, Runnable);
    }
}
