namespace Fgvm.Godot;

/// <summary>
///     One meaningful line of a Godot ConfigFile document.
/// </summary>
internal abstract record ConfigEntry
{
    /// <summary>A section header such as <c>[preset.0.options]</c>.</summary>
    internal sealed record Section(string Name, int Line) : ConfigEntry;

    /// <summary>A complete single-line assignment; <c>Value</c> is raw, undecoded text.</summary>
    internal sealed record Assignment(string? SectionName, string Key, string Value, int Line) : ConfigEntry;

    /// <summary>
    ///     An assignment whose value spans lines. Only the first physical line is retained, which is
    ///     enough for callers to diagnose a key that must not span lines.
    /// </summary>
    internal sealed record Multiline(string? SectionName, string Key, string Value, int Line) : ConfigEntry;

    /// <summary>A bracketed line that never closed.</summary>
    internal sealed record MalformedSection(string? SectionName, int Line) : ConfigEntry;

    /// <summary>A line that is neither a section header nor an assignment.</summary>
    internal sealed record MalformedAssignment(string? SectionName, int Line) : ConfigEntry;

    /// <summary>A value that opened a string or bracket and reached end of file still open.</summary>
    internal sealed record UnterminatedValue(string? SectionName, string Key, string Value, int Line) : ConfigEntry;
}

/// <summary>
///     Scans Godot ConfigFile documents (<c>project.godot</c>, <c>export_presets.cfg</c>) into a flat
///     entry stream. It resolves structure only: quote and bracket state are tracked across line
///     boundaries so a multiline Variant value can never be mistaken for a section or an assignment.
///     Value decoding, key policy, and diagnostic wording belong to callers.
/// </summary>
/// <remarks>
///     A value left open by a malformed document swallows the lines that follow it, exactly as
///     Godot's own parser would. Callers must treat <see cref="ConfigEntry.UnterminatedValue" /> as
///     a hard error, because everything it consumed is otherwise invisible. The residual case this
///     cannot detect is a stray quote that happens to rebalance partway through the file: parsing
///     resumes correctly, but the swallowed span is indistinguishable from legitimate string content.
/// </remarks>
internal ref struct ConfigScanner(ReadOnlySpan<char> content)
{
    private ReadOnlySpan<char> _remaining = content;
    private string? _section;
    private int _line;

    // The foreach pattern requires public members even though the scanner itself is internal.
    public ConfigEntry Current { get; private set; } = null!;

    public readonly ConfigScanner GetEnumerator() => this;

    public bool MoveNext()
    {
        while (!_remaining.IsEmpty)
        {
            var state = new ValueState();
            var line = state.Consume(NextLine()).Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            var openedAt = _line;

            if (line[0] == '[')
            {
                if (line is not ['[', .., ']'])
                {
                    Current = new ConfigEntry.MalformedSection(_section, openedAt);
                    return true;
                }

                _section = line[1..^1].Trim().ToString();
                Current = new ConfigEntry.Section(_section, openedAt);
                return true;
            }

            var separator = line.IndexOf('=');
            if (separator < 1)
            {
                Current = new ConfigEntry.MalformedAssignment(_section, openedAt);
                return true;
            }

            var key = line[..separator].Trim().ToString();
            var value = line[(separator + 1)..].Trim().ToString();

            if (!state.IsOpen)
            {
                Current = new ConfigEntry.Assignment(_section, key, value, openedAt);
                return true;
            }

            // The value opened a string or bracket that did not close on this line. Continuation
            // lines are value body, never structure, so they are consumed verbatim.
            while (state.IsOpen && !_remaining.IsEmpty)
            {
                state.Consume(NextLine());
            }

            Current = state.IsOpen
                ? new ConfigEntry.UnterminatedValue(_section, key, value, openedAt)
                : new ConfigEntry.Multiline(_section, key, value, openedAt);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Takes the next physical line, splitting on <c>\n</c> exactly as Godot does and
    ///     tolerating a trailing <c>\r</c>. Callers check <c>_remaining</c> first.
    /// </summary>
    private ReadOnlySpan<char> NextLine()
    {
        var index = _remaining.IndexOf('\n');
        ReadOnlySpan<char> line;

        if (index < 0)
        {
            line = _remaining;
            _remaining = default;
        }
        else
        {
            line = _remaining[..index];
            _remaining = _remaining[(index + 1)..];
        }

        _line++;
        return line is [.., '\r'] ? line[..^1] : line;
    }

    /// <summary>
    ///     Tracks whether a value is still open across physical lines.
    /// </summary>
    private struct ValueState
    {
        private int _depth;
        private bool _escaped;
        private bool _inString;

        internal readonly bool IsOpen => _inString || _depth > 0;

        /// <summary>
        ///     Consumes one physical line and returns the text preceding any comment. A <c>;</c>
        ///     begins a comment only outside a string, so separators inside values survive.
        /// </summary>
        internal ReadOnlySpan<char> Consume(ReadOnlySpan<char> line)
        {
            for (var index = 0; index < line.Length; index++)
            {
                if (_escaped)
                {
                    _escaped = false;
                    continue;
                }

                switch (line[index])
                {
                    case '\\' when _inString:
                        _escaped = true;
                        break;
                    case '"':
                        _inString = !_inString;
                        break;
                    case '(' or '[' or '{' when !_inString:
                        _depth++;
                        break;
                    case ')' or ']' or '}' when !_inString && _depth > 0:
                        _depth--;
                        break;
                    case ';' when !_inString:
                        return line[..index];
                }
            }

            // A string stays open across lines; a dangling escape does not.
            _escaped = false;
            return line;
        }
    }
}
