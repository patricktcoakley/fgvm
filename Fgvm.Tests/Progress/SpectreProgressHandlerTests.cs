using System.Globalization;
using System.Text.RegularExpressions;
using Fgvm.Cli.Progress;
using Fgvm.Extensions;
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
        var handler = new SpectreProgressHandler(_testConsole);
        const string installPathBase = "4.4.1-stable-standard";

        var result = await handler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase}..."));
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading,
                $"Downloading {installPathBase} • 50.0/100.0 MB • 25.5 MiB/s",
                BytesDownloaded: 50L * ByteSize.Megabyte,
                TotalBytes: 100L * ByteSize.Megabyte));
            progress.Complete();
            await Task.Delay(1);
            return "success";
        });

        Assert.Equal("success", result);
        var output = _testConsole.Output;
        Assert.Contains("Downloading", output);
        Assert.Contains("25.5 MiB/s", output);
    }

    [Fact]
    public async Task TrackProgressAsync_WritesVerboseDetailsWithoutReplacingStatus()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Downloading,
                "Downloading..."));
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Downloading,
                "Trying https://example.com/[mirror]...",
                IsVerboseDetail: true));
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.VerifyingChecksum,
                "Verifying checksum..."));
            progress.Complete();
            await Task.Delay(1);
            return true;
        });

        Assert.Contains("Trying https://example.com/[mirror]...", _testConsole.Output);
        Assert.Contains("Verifying checksum...", _testConsole.Output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldDisplayChecksumStage_WhenVerifyingChecksum()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        var result = await handler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.VerifyingChecksum, "Verifying checksum..."));
            progress.Complete();
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
        var handler = new SpectreProgressHandler(_testConsole);

        var result = await handler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Extracting, "Extracting files..."));
            progress.Complete();
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
        var handler = new SpectreProgressHandler(_testConsole);

        var result = await handler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.SettingDefault, "Setting as default version..."));
            progress.Complete();
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
        var handler = new SpectreProgressHandler(_testConsole);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await handler.TrackProgressAsync<string>(async session =>
            {
                var progress = session.AddOperation<InstallationStage>("Editor");
                progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, "Starting download..."));
                await Task.Delay(1);
                progress.Fail();
                throw new InvalidOperationException("Network error during download");
            });
        });

        var output = _testConsole.Output;
        Assert.Contains("Starting download", output);
    }

    [Fact]
    public async Task TrackProgressAsync_ShouldSequentiallyDisplayStages_WhenFullInstallationFlow()
    {
        var handler = new SpectreProgressHandler(_testConsole);
        const string installPathBase = "4.4.1-stable-standard";

        var result = await handler.TrackProgressAsync(async session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading, $"Downloading {installPathBase}..."));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Downloading,
                $"Downloading {installPathBase} • 126.7/126.7 MB • 58.0 MiB/s",
                BytesDownloaded: 1267L,
                TotalBytes: 1267L));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.VerifyingChecksum, "Verifying checksum..."));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.Extracting, "Extracting files..."));
            await Task.Delay(1);

            progress.Report(new OperationProgress<InstallationStage>(InstallationStage.SettingDefault, "Setting as default version..."));
            await Task.Delay(1);

            progress.Complete();
            return "installation_complete";
        });

        Assert.Equal("installation_complete", result);
        var output = _testConsole.Output;

        Assert.Contains("Downloading", output);
        Assert.Contains("58.0 MiB/s", output);
        Assert.Contains("Verifying checksum", output);
        Assert.Contains("Extracting files", output);
        Assert.Contains("Setting as default version", output);
    }

    [Fact]
    public async Task TrackProgressAsync_DisplaysMultipleOperationsAndKeepsIndependentTerminalStates()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(async session =>
        {
            var editor = session.AddOperation<InstallationStage>("Editor");
            var templates = session.AddOperation<TemplateInstallationStage>("Templates");
            editor.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Downloading,
                "Downloading",
                BytesDownloaded: 50,
                TotalBytes: 100));
            templates.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Downloading,
                "Downloading",
                BytesDownloaded: 10,
                TotalBytes: 100));
            editor.Complete();
            templates.Report(new OperationProgress<TemplateInstallationStage>(
                TemplateInstallationStage.Extracting,
                "Extracting"));
            templates.Cancel();
            await Task.Yield();
            return true;
        });

        Assert.Contains("Editor", _testConsole.Output);
        Assert.Contains("Completed", _testConsole.Output);
        Assert.Contains("Templates", _testConsole.Output);
        Assert.Contains("Canceled", _testConsole.Output);
    }

    [Fact]
    public async Task TrackProgressAsync_DoesNotDisplayOperationThatOnlyReportsTerminalState()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(session =>
        {
            session.AddOperation<InstallationStage>("Editor").Complete();
            return Task.FromResult(true);
        });

        Assert.DoesNotContain("Editor", _testConsole.Output);
    }

    [Fact]
    public async Task TrackProgressAsync_PreservesMeasuredProgressWithinTheSameStage()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Downloading,
                "Downloading",
                BytesDownloaded: 40,
                TotalBytes: 100));
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Downloading,
                "Retrying download"));
            progress.Cancel();
            return Task.FromResult(true);
        });

        Assert.Matches(@"Editor • Canceled[^\r\n]*\s40%", _testConsole.Output);
    }

    // RangeDownloadProgress guarantees a monotonic sequence of byte counts; this covers the other half of that
    // chain, that the handler renders them in the order it receives them rather than reordering or dropping any.
    [Fact]
    public async Task TrackProgressAsync_RendersRisingByteCountsAsRisingPercentages()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            foreach (var downloaded in new long[] { 10, 25, 40, 55, 70, 85, 100 })
            {
                progress.Report(new OperationProgress<InstallationStage>(
                    InstallationStage.Downloading,
                    $"Downloading {downloaded}",
                    BytesDownloaded: downloaded,
                    TotalBytes: 100));
            }

            progress.Complete();
            return Task.FromResult(true);
        });

        var percentages = RenderedPercentages(_testConsole.Output);

        Assert.NotEmpty(percentages);
        Assert.Equal(percentages.OrderBy(percentage => percentage), percentages);
        Assert.Equal(100, percentages[^1]);
    }

    // Defence in depth for the bar itself: whatever a producer does, a rendered percentage never slides backwards.
    [Fact]
    public async Task TrackProgressAsync_NeverRendersAPercentageLowerThanOneAlreadyShown()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            foreach (var downloaded in new long[] { 10, 60, 30, 90, 50, 80 })
            {
                progress.Report(new OperationProgress<InstallationStage>(
                    InstallationStage.Downloading,
                    $"Downloading {downloaded}",
                    BytesDownloaded: downloaded,
                    TotalBytes: 100));
            }

            progress.Complete();
            return Task.FromResult(true);
        });

        var percentages = RenderedPercentages(_testConsole.Output);

        Assert.NotEmpty(percentages);
        Assert.Equal(percentages.OrderBy(percentage => percentage), percentages);
        // The highest value reported still wins; dropping the stale reports must not cap the bar.
        Assert.Equal(100, percentages[^1]);
    }

    [Fact]
    public async Task TrackProgressAsync_ResetsMeasuredProgressWhenStageChanges()
    {
        var handler = new SpectreProgressHandler(_testConsole);

        await handler.TrackProgressAsync(session =>
        {
            var progress = session.AddOperation<InstallationStage>("Editor");
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Downloading,
                "Downloading",
                BytesDownloaded: 100,
                TotalBytes: 100));
            progress.Report(new OperationProgress<InstallationStage>(
                InstallationStage.Extracting,
                "Extracting"));
            progress.Cancel();
            return Task.FromResult(true);
        });

        Assert.Matches(@"Editor • Canceled[^\r\n]*\s0%", _testConsole.Output);
    }

    [Fact]
    public async Task TrackProgressAsync_TreatsTheFirstReportAsANewStage_EvenInTheDefaultStage()
    {
        // Tracking the first report by testing for null used to skip the reset for operations starting in stage 0
        var defaultStageOutput = await RenderSingleReport(InstallationStage.Initializing);
        var laterStageOutput = await RenderSingleReport(InstallationStage.Downloading);

        // An indeterminate bar is a colour gradient; a determinate 0% bar is one flat colour
        Assert.True(BarColours(laterStageOutput) > 2, "expected a later stage to render an indeterminate bar");
        Assert.True(BarColours(defaultStageOutput) > 2, "expected the default stage to render an indeterminate bar");
    }

    private static int BarColours(string output) =>
        Regex.Matches(output, @"\[38;2;\d+;\d+;\d+m")
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .Count();

    // The percentage column is the only place the handler's numeric progress reaches the screen.
    private static int[] RenderedPercentages(string output) =>
        Regex.Matches(output, @"(?<percentage>\d{1,3})%")
            .Select(match => int.Parse(match.Groups["percentage"].Value, CultureInfo.InvariantCulture))
            .ToArray();

    private static async Task<string> RenderSingleReport(InstallationStage stage)
    {
        var console = new TestConsole
        {
            Profile = { Capabilities = { Interactive = true } }
        };
        console.EmitAnsiSequences();

        await new SpectreProgressHandler(console).TrackProgressAsync(session =>
        {
            session.AddOperation<InstallationStage>("Editor")
                .Report(new OperationProgress<InstallationStage>(stage, "Working"));
            return Task.FromResult(true);
        });

        return console.Output;
    }
}
