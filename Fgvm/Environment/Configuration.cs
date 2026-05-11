using Fgvm.Types;
using Microsoft.Extensions.Configuration;

namespace Fgvm.Environment;

public static class Configuration
{
    public static Result<Unit, ConfigError> ValidateConfiguration(IConfiguration configuration)
    {
        var githubToken = configuration["github:token"];

        if (string.IsNullOrEmpty(githubToken))
        {
            return new Result<Unit, ConfigError>.Success(Unit.Value);
        }

        // GitHub token format validation per Microsoft Purview specification
        // Valid prefixes: ghp_, gho_, ghu_, ghs_, ghr_
        if (!githubToken.StartsWith("ghp_") && !githubToken.StartsWith("gho_") &&
            !githubToken.StartsWith("ghu_") && !githubToken.StartsWith("ghs_") &&
            !githubToken.StartsWith("ghr_"))
        {
            return new Result<Unit, ConfigError>.Failure(new ConfigError.InvalidGitHubTokenPrefix());
        }

        // https://learn.microsoft.com/en-us/purview/sit-defn-github-personal-access-token
        if (githubToken.Length != 40)
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
