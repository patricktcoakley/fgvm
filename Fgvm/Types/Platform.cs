using System.Runtime.InteropServices;
using Fgvm.Environment;
using Fgvm.Godot;

namespace Fgvm.Types;

/// <summary>
///     Represents the possible errors for platform detection.
/// </summary>
public abstract record PlatformError
{
    public record Unsupported(Release Release, OS OS, Architecture Architecture) : PlatformError;
}
