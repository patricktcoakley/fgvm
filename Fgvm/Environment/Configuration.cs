using Fgvm.Types;
using Microsoft.Extensions.Configuration;

namespace Fgvm.Environment;

public static class Configuration
{
    private const string FineGrainedPersonalAccessTokenPrefix = "github_pat_";

    private static readonly string[] ClassicTokenPrefixes = ["ghp_", "gho_", "ghu_", "ghs_", "ghr_"];

    public static Result<Unit, ConfigError> ValidateConfiguration(IConfiguration configuration)
    {
        var githubToken = configuration["github:token"];

        if (string.IsNullOrEmpty(githubToken))
        {
            return new Result<Unit, ConfigError>.Success(Unit.Value);
        }

        if (!ClassicTokenPrefixes.Any(prefix => githubToken.StartsWith(prefix, StringComparison.Ordinal)) &&
            !githubToken.StartsWith(FineGrainedPersonalAccessTokenPrefix, StringComparison.Ordinal))
        {
            return new Result<Unit, ConfigError>.Failure(new ConfigError.InvalidGitHubTokenPrefix());
        }

        // https://learn.microsoft.com/en-us/purview/sit-defn-github-personal-access-token
        if (ClassicTokenPrefixes.Any(prefix => githubToken.StartsWith(prefix, StringComparison.Ordinal)) &&
            githubToken.Length != 40)
        {
            return new Result<Unit, ConfigError>.Failure(new ConfigError.InvalidGitHubTokenLength());
        }

        if (githubToken.StartsWith(FineGrainedPersonalAccessTokenPrefix, StringComparison.Ordinal) &&
            githubToken.Length == FineGrainedPersonalAccessTokenPrefix.Length)
        {
            return new Result<Unit, ConfigError>.Failure(new ConfigError.InvalidGitHubTokenLength());
        }

        // Check that the token contains only valid characters (alphanumeric + underscore)
        if (!githubToken.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return new Result<Unit, ConfigError>.Failure(new ConfigError.InvalidGitHubTokenCharacters());
        }

        return new Result<Unit, ConfigError>.Success(Unit.Value);
    }
}
