using Fgvm.Environment;
using Fgvm.Godot;
using System.Runtime.InteropServices;

namespace Fgvm.Types;

/// <summary>
///     Represents failures while creating a release value from user or project input.
/// </summary>
public abstract record ReleaseParseError
{
    public sealed record EmptyVersion : ReleaseParseError;

    public sealed record InvalidVersion(string Version) : ReleaseParseError;

    public sealed record UnsupportedPlatform(Release Release, OS OS, Architecture Architecture) : ReleaseParseError;
}
