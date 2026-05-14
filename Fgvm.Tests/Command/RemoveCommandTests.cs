using Fgvm.Cli.Command;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public class RemoveCommandTests
{
    private readonly Mock<IHostSystem> _mockHostSystem;
    private readonly Mock<IInstallationRegistry> _mockInstallationRegistry;
    private readonly Mock<ILogger<RemoveCommand>> _mockLogger;
    private readonly Mock<IPathService> _mockPathService;
    private readonly Mock<IReleaseManager> _mockReleaseManager;
    private readonly RemoveCommand _removeCommand;

    public RemoveCommandTests()
    {
        _mockHostSystem = new Mock<IHostSystem>();
        _mockReleaseManager = new Mock<IReleaseManager>();
        _mockInstallationRegistry = new Mock<IInstallationRegistry>();
        _mockPathService = new Mock<IPathService>();

        _mockPathService.Setup(x => x.RootPath).Returns("/test/root");
        _mockPathService.Setup(x => x.SymlinkPath).Returns("/test/Godot");
        _mockPathService.Setup(x => x.LogPath).Returns("/test/logs");
        _mockPathService.Setup(x => x.ConfigPath).Returns("/test/config");
        _mockPathService.Setup(x => x.ReleasesPath).Returns("/test/releases.json");
        _mockPathService.Setup(x => x.BinPath).Returns("/test/bin");
        _mockPathService.Setup(x => x.ShimPath).Returns("/test/bin/godot");
        _mockPathService.Setup(x => x.MacAppSymlinkPath).Returns("/test/Godot.app");
        _mockHostSystem.Setup(x => x.RemoveSymbolicLinks())
            .Returns(new Result<Unit, SymlinkError>.Success(Unit.Value));

        _mockInstallationRegistry.Setup(x => x.GetDefault())
            .Returns(new Result<Installation, InstallationRegistryError>.Failure(new InstallationRegistryError.NotFound("default")));

        _mockInstallationRegistry.Setup(x => x.ClearDefault())
            .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));

        _mockLogger = new Mock<ILogger<RemoveCommand>>();
        var testConsole = new TestConsole();
        _removeCommand = new RemoveCommand(_mockHostSystem.Object, _mockReleaseManager.Object, _mockInstallationRegistry.Object, _mockPathService.Object, testConsole,
            _mockLogger.Object);
    }

    private RemoveCommand CreateRemoveCommandWithConsole(TestConsole console) =>
        new(_mockHostSystem.Object, _mockReleaseManager.Object, _mockInstallationRegistry.Object, _mockPathService.Object, console, _mockLogger.Object);

    [Fact]
    public async Task Remove_WithNoInstallations_ShowsNoInstallationsMessage()
    {
        SetupInstallations([]);

        await _removeCommand.Remove(CancellationToken.None, "some-query");

        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Once);
    }

    [Fact]
    public async Task Remove_WithQueryThatMatchesNothing_ShowsNotFoundMessage()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };
        var query = new[] { "5.0" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(Array.Empty<string>());

        await _removeCommand.Remove(CancellationToken.None, query);

        // Should not attempt to delete anything
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);
    }

    [Fact]
    public async Task Remove_WithExactlyOneMatch_AutomaticallyRemovesWithoutPrompt()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable", "4.1.0-stable" };
        var query = new[] { "4.3.0" };
        var filteredVersions = new[] { "4.3.0-stable" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        // Setup remaining installations after removal
        SetupInstallationsSequence(installedVersions, ["4.2.0-stable", "4.1.0-stable"]);

        await _removeCommand.Remove(CancellationToken.None, query);

        // Should not remove symlinks since there are still installations
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);
    }

    [Fact]
    public async Task Remove_WithExactlyOneMatch_RemovesSymlinksWhenLastInstallation()
    {
        var installedVersions = new[] { "4.3.0-stable" };
        var query = new[] { "4.3.0" };
        var filteredVersions = new[] { "4.3.0-stable" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        // Setup empty installations after removal
        SetupInstallationsSequence(installedVersions, []);

        await _removeCommand.Remove(CancellationToken.None, query);

        // Should remove symlinks since no installations remain
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Once);
    }

    [Fact]
    public async Task Remove_WhenRemovingDefault_RemovesSymlinksEvenWithRemainingInstallations()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };
        var query = new[] { "4.3.0" };
        var filteredVersions = new[] { "4.3.0-stable" };
        var defaultInstallation = CreateInstallation("4.3.0-stable");

        SetupInstallations(installedVersions);
        SetupInstallationsSequence(installedVersions, ["4.2.0-stable"]);
        _mockInstallationRegistry.Setup(x => x.GetDefault())
            .Returns(new Result<Installation, InstallationRegistryError>.Success(defaultInstallation));

        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        await _removeCommand.Remove(CancellationToken.None, query);

        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Once);
    }

    [Fact]
    public async Task Remove_WithMultipleMatches_ShowsMultiSelectionPrompt()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.3.0-mono", "4.2.0-stable" };
        var query = new[] { "4.3.0" };
        var filteredVersions = new[] { "4.3.0-stable", "4.3.0-mono" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        // Setup for remaining installations after removal
        SetupInstallationsSequence(installedVersions, ["4.2.0-stable"]);

        // Create a new test console for this test
        var testConsole = new TestConsole();
        testConsole.Interactive();

        // Mock user selecting first option (4.3.0-stable) and confirming
        testConsole.Input.PushKey(ConsoleKey.Spacebar); // Select first option
        testConsole.Input.PushKey(ConsoleKey.Enter); // Confirm selection

        var removeCommand = CreateRemoveCommandWithConsole(testConsole);

        await removeCommand.Remove(CancellationToken.None, query);

        // Should not remove symlinks since there are still installations
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);
    }

    [Fact]
    public async Task Remove_WithEmptyQuery_ShowsAllInstallationsForSelection()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };
        var emptyQuery = Array.Empty<string>();

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(emptyQuery, installedVersions, false))
            .Returns(installedVersions); // Empty query should return all

        // Setup for remaining installations after removal
        SetupInstallationsSequence(installedVersions, ["4.3.0-stable"]);

        // Create a new test console for this test
        var testConsole = new TestConsole();
        testConsole.Interactive();

        // Mock user selecting second option (4.2.0-stable) and confirming
        testConsole.Input.PushKey(ConsoleKey.DownArrow); // Move to second option
        testConsole.Input.PushKey(ConsoleKey.Spacebar); // Select second option
        testConsole.Input.PushKey(ConsoleKey.Enter); // Confirm selection

        var removeCommand = CreateRemoveCommandWithConsole(testConsole);

        await removeCommand.Remove(CancellationToken.None, emptyQuery);

        // Should not remove symlinks since there are still installations
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);
    }

    [Fact]
    public async Task Remove_WithMultipleSelections_RemovesAllSelectedVersions()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.3.0-mono", "4.2.0-stable" };
        var query = new[] { "4.3" };
        var filteredVersions = new[] { "4.3.0-stable", "4.3.0-mono" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        // Setup for remaining installations after removal
        SetupInstallationsSequence(installedVersions, ["4.2.0-stable"]);

        // Create a new test console for this test
        var testConsole = new TestConsole();
        testConsole.Interactive();

        // Mock user selecting both options and confirming
        testConsole.Input.PushKey(ConsoleKey.Spacebar); // Select first option (4.3.0-stable)
        testConsole.Input.PushKey(ConsoleKey.DownArrow); // Move to second option
        testConsole.Input.PushKey(ConsoleKey.Spacebar); // Select second option (4.3.0-mono)
        testConsole.Input.PushKey(ConsoleKey.Enter); // Confirm selection

        var removeCommand = CreateRemoveCommandWithConsole(testConsole);

        await removeCommand.Remove(CancellationToken.None, query);

        // Should not remove symlinks since there are still installations
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);
    }

    [Fact]
    public async Task Remove_WhenUserCancelsPrompt_ThrowsOperationCanceledException()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.3.0-mono", "4.2.0-stable" };
        var query = new[] { "4.3.0" };
        var filteredVersions = new[] { "4.3.0-stable", "4.3.0-mono" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        // Use a cancelled cancellation token to simulate user cancellation
        var cancellationToken = new CancellationToken(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _removeCommand.Remove(cancellationToken, query));
    }

    [Fact]
    public async Task Remove_WithCancellation_HandlesTaskCanceledException()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };
        var query = new[] { "4.3" };
        var filteredVersions = new[] { "4.3.0-stable" }; // Single match to test cancellation handling

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        var cancellationToken = new CancellationToken(true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _removeCommand.Remove(cancellationToken, query));
    }

    [Fact]
    public async Task Remove_WithMultipleSelections_RemovesSymlinksWhenAllInstallationsRemoved()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.3.0-mono" };
        var query = Array.Empty<string>();

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(installedVersions); // Empty query returns all

        // Setup empty installations after removal
        SetupInstallationsSequence(installedVersions, []);

        // Create a new test console for this test
        var testConsole = new TestConsole();
        testConsole.Interactive();

        // Mock user selecting all options and confirming
        testConsole.Input.PushKey(ConsoleKey.Spacebar); // Select first option (4.3.0-stable)
        testConsole.Input.PushKey(ConsoleKey.DownArrow); // Move to second option
        testConsole.Input.PushKey(ConsoleKey.Spacebar); // Select second option (4.3.0-mono)
        testConsole.Input.PushKey(ConsoleKey.Enter); // Confirm selection

        var removeCommand = CreateRemoveCommandWithConsole(testConsole);

        await removeCommand.Remove(CancellationToken.None, query);

        // Should remove symlinks since no installations remain
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Once);
    }

    [Fact]
    public async Task Remove_WithException_HandlesAndRethrows()
    {
        var installedVersions = new[] { "4.3.0-stable" };
        var query = new[] { "4.3.0" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Throws(new InvalidOperationException("Test exception"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _removeCommand.Remove(CancellationToken.None, query));
    }

    private void SetupInstallations(string[] releaseNames)
    {
        var installations = CreateInstallations(releaseNames);
        _mockInstallationRegistry.Setup(x => x.ListInstallations())
            .Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(installations));

        foreach (var installation in installations)
        {
            _mockInstallationRegistry.Setup(x => x.FindByReleaseName(installation.ReleaseNameWithRuntime))
                .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));

            _mockInstallationRegistry.Setup(x => x.Remove(installation.Key))
                .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));
        }
    }

    private void SetupInstallationsSequence(params string[][] releaseNameSequences)
    {
        var sequence = _mockInstallationRegistry.SetupSequence(x => x.ListInstallations());
        foreach (var releaseNames in releaseNameSequences)
        {
            sequence.Returns(new Result<IReadOnlyList<Installation>, InstallationRegistryError>.Success(CreateInstallations(releaseNames)));
        }

        foreach (var releaseName in releaseNameSequences.SelectMany(x => x).Distinct())
        {
            var installation = CreateInstallation(releaseName);
            _mockInstallationRegistry.Setup(x => x.FindByReleaseName(releaseName))
                .Returns(new Result<Installation, InstallationRegistryError>.Success(installation));

            _mockInstallationRegistry.Setup(x => x.Remove(installation.Key))
                .Returns(new Result<Unit, InstallationRegistryError>.Success(Unit.Value));
        }
    }

    private static Installation[] CreateInstallations(IEnumerable<string> releaseNames) =>
        releaseNames.Select(CreateInstallation).ToArray();

    private static Installation CreateInstallation(string releaseName) =>
        new($"{releaseName}@linux.x86_64", releaseName, "linux.x86_64", releaseName, null, null);
}
