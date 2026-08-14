// Test-only CLI composition root published as a .NET file-based app.
#:property TargetFramework=net10.0
#:property RuntimeFrameworkVersion=10.0.10
#:property LangVersion=14
#:property AssemblyName=fgvm-test-host
#:property Version=2.4.0
#:property PublishAot=true
#:property SelfContained=true
#:property EnableCompressionInSingleFile=true
#:property StripSymbols=true
#:property PublishTrimmed=true
#:property TrimMode=full
#:property InvariantGlobalization=true
#:property OptimizationPreference=Speed
#:project ../../Fgvm.Cli/Fgvm.Cli.csproj
#:project ../../Fgvm.Tests.Fixtures/Fgvm.Tests.Fixtures.csproj

using System.Runtime.InteropServices;
using Fgvm.Cli;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

const string architectureOverrideVariable = "FGVM_INTEGRATION_ARCH_OVERRIDE";
const string fixtureManifestVariable = "FGVM_INTEGRATION_FIXTURE_MANIFEST";

var fixtureManifestPath = System.Environment.GetEnvironmentVariable(fixtureManifestVariable);
if (string.IsNullOrWhiteSpace(fixtureManifestPath))
{
    throw new InvalidOperationException($"{fixtureManifestVariable} is not set.");
}

var systemInfo = CreateSystemInfo(architectureOverrideVariable);
return CliApplication.Run(args, serviceCollection =>
{
    serviceCollection.AddSingleton(systemInfo);
    serviceCollection.AddSingleton<IDownloadClient>(serviceProvider =>
        new FixtureDownloadClient(
            fixtureManifestPath,
            serviceProvider.GetRequiredService<ILogger<FixtureDownloadClient>>()));
});

static SystemInfo CreateSystemInfo(string architectureOverrideVariable)
{
    var systemInfo = new SystemInfo();
    if (System.Environment.GetEnvironmentVariable(architectureOverrideVariable) is not { Length: > 0 } overrideValue)
    {
        return systemInfo;
    }

    var architecture = overrideValue.ToLowerInvariant() switch
    {
        "x64" => Architecture.X64,
        "arm64" => Architecture.Arm64,
        _ => throw new InvalidOperationException($"{architectureOverrideVariable} must be either 'x64' or 'arm64'.")
    };

    return new SystemInfo(systemInfo.CurrentOS, architecture);
}
