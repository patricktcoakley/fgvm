using Fgvm.Cli.Progress;
using Fgvm.Progress;
using Fgvm.Services;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Progress;

public class SpectreProgressHandlerTests
{
    private readonly TestConsole _testConsole;

    public SpectreProgressHandlerTests()
    {
        _testConsole = new TestConsole
        {
            Profile = { Capabilities = { Interactive = true } }
        };

        _testConsole.EmitAnsiSequences();
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldDisplayDownloadProgress_WhenDownloadingStage()
    {
        var handler = new SpectreProgressHandler<InstallationStage>(_testConsole);
        const string installPathBase = "4.4.1-stable-standard";

        var result = await handler.TrackProgressAsync(async progress =>
        {
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase}..."));
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase} • 50.0/100.0 MB • 25.5 MB/s"));
            await Task.Delay(1);
            return "success";
        });

        Assert.Equal("success", result);
        var output = _testConsole.Output;
        Assert.Contains("Downloading", output);
        Assert.Contains("25.5 MB/s", output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldDisplayChecksumStage_WhenVerifyingChecksum()
    {
        var handler = new SpectreProgressHandler<InstallationStage>(_testConsole);

        var result = await handler.TrackProgressAsync(async progress =>
        {
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.VerifyingChecksum, "Verifying checksum..."));
            await Task.Delay(1);
            return "checksum_verified";
        });

        Assert.Equal("checksum_verified", result);
        var output = _testConsole.Output;
        Assert.Contains("Verifying checksum", output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldDisplayExtractionStage_WhenExtracting()
    {
        var handler = new SpectreProgressHandler<InstallationStage>(_testConsole);

        var result = await handler.TrackProgressAsync(async progress =>
        {
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Extracting, "Extracting files..."));
            await Task.Delay(1);
            return "extracted";
        });

        Assert.Equal("extracted", result);
        var output = _testConsole.Output;
        Assert.Contains("Extracting files", output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldDisplaySettingDefaultStage_WhenSettingDefault()
    {
        var handler = new SpectreProgressHandler<InstallationStage>(_testConsole);

        var result = await handler.TrackProgressAsync(async progress =>
        {
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.SettingDefault, "Setting as default version..."));
            await Task.Delay(1);
            return "default_set";
        });

        Assert.Equal("default_set", result);
        var output = _testConsole.Output;
        Assert.Contains("Setting as default version", output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldHandleExceptions_WhenOperationFails()
    {
        var handler = new SpectreProgressHandler<InstallationStage>(_testConsole);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await handler.TrackProgressAsync<string>(async progress =>
            {
                progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, "Starting download..."));
                await Task.Delay(1);
                throw new InvalidOperationException("Network error during download");
            });
        });

        var output = _testConsole.Output;
        Assert.Contains("Starting download", output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldSequentiallyDisplayStages_WhenFullInstallationFlow()
    {
        var handler = new SpectreProgressHandler<InstallationStage>(_testConsole);
        const string installPathBase = "4.4.1-stable-standard";

        var result = await handler.TrackProgressAsync(async progress =>
        {
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase}..."));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase} • 126.7/126.7 MB • 58.0 MB/s"));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.VerifyingChecksum, "Verifying checksum..."));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Extracting, "Extracting files..."));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.SettingDefault, "Setting as default version..."));
            await Task.Delay(1);

            return "installation_complete";
        });

        Assert.Equal("installation_complete", result);
        var output = _testConsole.Output;

        Assert.Contains("Downloading", output);
        Assert.Contains("58.0 MB/s", output);
        Assert.Contains("Verifying checksum", output);
        Assert.Contains("Extracting files", output);
        Assert.Contains("Setting as default version", output);
    }
}
