namespace Fgvm.Types;

/// <summary>
///     Represents configuration validation failures.
/// </summary>
public abstract record ConfigError
{
    public sealed record InvalidGitHubTokenPrefix : ConfigError;

    public sealed record InvalidGitHubTokenLength : ConfigError;

    public sealed record InvalidGitHubTokenCharacters : ConfigError;
}
