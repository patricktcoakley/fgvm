using Fgvm.Environment;
using Fgvm.Types;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;

namespace Fgvm.Tests.Environment;

public class ConfigurationTests
{
    private static string GenerateTestToken(string prefix = "ghp")
    {
        var remainingLength = 40 - prefix.Length - 1; // -1 for the underscore
        var hexString = RandomNumberGenerator.GetHexString(remainingLength, true);
        return $"{prefix}_{hexString}";
    }

    private static string GenerateFineGrainedToken() =>
        $"github_pat_{RandomNumberGenerator.GetHexString(82, true)}";

    private static string GenerateInvalidToken(string prefix, char invalidChar, int position = 20)
    {
        var validToken = GenerateTestToken(prefix);
        var chars = validToken.ToCharArray();
        chars[4 + position] = invalidChar; // Skip the "ghp_" prefix
        return new string(chars);
    }

    [Fact]
    public void ValidateConfiguration_ValidToken_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = GenerateTestToken()
            })
            .Build();

        Assert.IsType<Result<Unit, ConfigError>.Success>(Configuration.ValidateConfiguration(config));
    }

    [Fact]
    public void ValidateConfiguration_NoToken_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        Assert.IsType<Result<Unit, ConfigError>.Success>(Configuration.ValidateConfiguration(config));
    }

    [Fact]
    public void ValidateConfiguration_EmptyToken_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = ""
            })
            .Build();

        Assert.IsType<Result<Unit, ConfigError>.Success>(Configuration.ValidateConfiguration(config));
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("xyz")]
    [InlineData("token")]
    public void ValidateConfiguration_InvalidPrefix_ReturnsConfigError(string prefix)
    {
        var token = GenerateTestToken(prefix);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = token
            })
            .Build();

        var failure = Assert.IsType<Result<Unit, ConfigError>.Failure>(Configuration.ValidateConfiguration(config));
        Assert.IsType<ConfigError.InvalidGitHubTokenPrefix>(failure.Error);
    }

    [Theory]
    [InlineData("ghp_short")]
    [InlineData("ghp_")]
    [InlineData("github_pat_")]
    public void ValidateConfiguration_InvalidLength_ReturnsConfigError(string token)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = token
            })
            .Build();

        var failure = Assert.IsType<Result<Unit, ConfigError>.Failure>(Configuration.ValidateConfiguration(config));
        Assert.IsType<ConfigError.InvalidGitHubTokenLength>(failure.Error);
    }

    [Theory]
    [InlineData('@')]
    [InlineData('#')]
    [InlineData(' ')]
    [InlineData('-')]
    public void ValidateConfiguration_InvalidCharacters_ReturnsConfigError(char invalidChar)
    {
        var token = GenerateInvalidToken("ghp", invalidChar);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = token
            })
            .Build();

        var failure = Assert.IsType<Result<Unit, ConfigError>.Failure>(Configuration.ValidateConfiguration(config));
        Assert.IsType<ConfigError.InvalidGitHubTokenCharacters>(failure.Error);
    }

    [Theory]
    [InlineData("ghp")]
    [InlineData("gho")]
    [InlineData("ghu")]
    [InlineData("ghs")]
    [InlineData("ghr")]
    public void ValidateConfiguration_ValidTokens_DoesNotThrow(string prefix)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = GenerateTestToken(prefix)
            })
            .Build();

        Assert.IsType<Result<Unit, ConfigError>.Success>(Configuration.ValidateConfiguration(config));
    }

    [Fact]
    public void ValidateConfiguration_ValidFineGrainedToken_DoesNotThrow()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["github:token"] = GenerateFineGrainedToken()
            })
            .Build();

        Assert.IsType<Result<Unit, ConfigError>.Success>(Configuration.ValidateConfiguration(config));
    }
}
