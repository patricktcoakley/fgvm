using Fgvm.Cli.Command;
using Fgvm.Cli.Services;
using Fgvm.Environment;
using Fgvm.Godot;
using Fgvm.Services;
using Fgvm.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Spectre.Console.Testing;

namespace Fgvm.Tests.Command;

public class RemoveCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-remove-command-tests", Guid.NewGuid().ToString("N"));
    private readonly Mock<IHostSystem> _mockHostSystem;
    private readonly Mock<IInstallationRegistry> _mockInstallationRegistry;
    private readonly Mock<ILogger<RemoveCommand>> _mockLogger;
    private readonly Mock<IGodotPathService> _mockGodotPathService;
    private readonly Mock<IPathService> _mockPathService;
    private readonly Mock<IReleaseManager> _mockReleaseManager;
    private readonly Mock<ITemplateOrchestrator> _mockTemplateOrchestrator;
    private readonly Mock<ITemplateRegistry> _mockTemplateRegistry;
    private readonly RemoveCommand _removeCommand;

    public RemoveCommandTests()
    {
        Directory.CreateDirectory(_root);
        _mockHostSystem = new Mock<IHostSystem>();
        _mockReleaseManager = new Mock<IReleaseManager>();
        _mockInstallationRegistry = new Mock<IInstallationRegistry>();
        _mockTemplateOrchestrator = new Mock<ITemplateOrchestrator>();
        _mockTemplateRegistry = new Mock<ITemplateRegistry>();
        _mockPathService = new Mock<IPathService>();
        _mockGodotPathService = new Mock<IGodotPathService>();
        _mockGodotPathService.Setup(x => x.ExportTemplatesRootPath).Returns(Path.Combine(_root, "templates"));

        _mockPathService.Setup(x => x.RootPath).Returns(_root);
        _mockPathService.Setup(x => x.InstallationsPath).Returns(Path.Combine(_root, "installations.json"));
        _mockPathService.Setup(x => x.SymlinkPath).Returns("/test/Godot");
        _mockPathService.Setup(x => x.LogPath).Returns("/test/logs");
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
        _mockTemplateOrchestrator.Setup(x => x.SelectForRemovalAsync(
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Result<IReadOnlyList<TemplateInstallation>, TemplateRegistryError>.Success([]));
        _mockTemplateRegistry.Setup(x => x.FindByReleaseName(It.IsAny<string>()))
            .Returns((string releaseNameWithRuntime) =>
                new Result<TemplateInstallation, TemplateRegistryError>.Failure(
                    new TemplateRegistryError.NotFound(releaseNameWithRuntime)));

        _mockLogger = new Mock<ILogger<RemoveCommand>>();
        _removeCommand = CreateRemoveCommandWithConsole(new TestConsole());
    }

    // Real service so staging runs against the temp root
    private RemoveCommand CreateRemoveCommandWithConsole(TestConsole console) =>
        new(_mockHostSystem.Object, _mockReleaseManager.Object, _mockInstallationRegistry.Object,
            _mockTemplateRegistry.Object, _mockTemplateOrchestrator.Object,
            CreateRemovalService(console),
            _mockPathService.Object, console,
            _mockLogger.Object);

    [Fact]
    public async Task Remove_WithNoInstallations_ShowsNoInstallationsMessage()
    {
        SetupInstallations([]);

        await _removeCommand.Remove(cancellationToken: CancellationToken.None, query: ["some-query"]);

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

        await _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

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

        await _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

        // Should not remove symlinks since there are still installations
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);
    }

    [Fact]
    public async Task Remove_WithTemplates_RemovesTemplatesForSelectedEditor()
    {
        var installedVersions = new[] { "4.3.0-stable-standard", "4.2.0-stable-standard" };
        var query = new[] { "4.3" };
        var selectedVersion = installedVersions[0];

        SetupInstallationsSequence(installedVersions, [installedVersions[1]]);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns([selectedVersion]);

        await _removeCommand.Remove(
            withTemplates: true,
            cancellationToken: CancellationToken.None,
            query: query);

        _mockTemplateRegistry.Verify(x => x.FindByReleaseName(selectedVersion), Times.Once);
        _mockTemplateRegistry.Verify(x => x.FindByReleaseName(installedVersions[1]), Times.Never);

        // Looked up directly; the orchestrator prints and prompts
        _mockTemplateOrchestrator.Verify(x => x.SelectForRemovalAsync(
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Remove_WithTemplatesAndNoEditors_StillRemovesMatchingTemplates()
    {
        var query = new[] { "4.3" };
        SetupInstallations([]);

        await _removeCommand.Remove(
            withTemplates: true,
            cancellationToken: CancellationToken.None,
            query: query);

        _mockTemplateOrchestrator.Verify(x => x.SelectForRemovalAsync(
                It.Is<string[]>(templateQuery => templateQuery.SequenceEqual(query)),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Remove_WithoutTemplates_DoesNotRemoveTemplates()
    {
        var installedVersions = new[] { "4.3.0-stable-standard" };
        var query = new[] { "4.3" };
        SetupInstallationsSequence(installedVersions, []);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(installedVersions);

        await _removeCommand.Remove(
            cancellationToken: CancellationToken.None,
            query: query);

        _mockTemplateOrchestrator.Verify(x => x.SelectForRemovalAsync(
                It.IsAny<string[]>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Remove_WithTemplates_RemovesEditorAndTemplateOnlyAfterCommit()
    {
        var version = "4.3.0-stable-standard";
        var query = new[] { "4.3" };
        var installation = CreateInstallation(version);
        var editorPath = Path.Combine(_root, installation.RelativePath);
        var templatePath = Path.Combine(_root, "templates", "4.3.stable");
        Directory.CreateDirectory(editorPath);
        Directory.CreateDirectory(templatePath);
        File.WriteAllText(Path.Combine(_root, "installations.json"), "before");

        SetupInstallationsSequence([version], []);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, new[] { version }, false))
            .Returns([version]);
        _mockHostSystem.Setup(x => x.DirectoryExists(editorPath))
            .Returns(new Result<bool, FileOperationError>.Success(true));
        SetupTemplateFor(version, templatePath);

        await _removeCommand.Remove(
            withTemplates: true,
            cancellationToken: CancellationToken.None,
            query: query);

        Assert.False(Directory.Exists(editorPath));
        Assert.False(Directory.Exists(templatePath));
        Assert.Empty(Tombstones());
    }

    [Fact]
    public async Task Remove_WhenAlreadyCancelled_DoesNotStart()
    {
        var version = "4.3.0-stable-standard";
        var query = new[] { "4.3" };
        var installation = CreateInstallation(version);
        var editorPath = Path.Combine(_root, installation.RelativePath);
        Directory.CreateDirectory(editorPath);

        SetupInstallations([version]);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, new[] { version }, false))
            .Returns([version]);
        _mockHostSystem.Setup(x => x.DirectoryExists(editorPath))
            .Returns(new Result<bool, FileOperationError>.Success(true));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _removeCommand.Remove(
                cancellationToken: new CancellationToken(true),
                query: query));

        Assert.True(Directory.Exists(editorPath));
        Assert.Empty(Tombstones());
        _mockInstallationRegistry.Verify(x => x.Remove(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Remove_WhenCancelledAfterSelection_StillRemovesTheInstallation()
    {
        var version = "4.3.0-stable-standard";
        var query = new[] { "4.3" };
        var installation = CreateInstallation(version);
        var editorPath = Path.Combine(_root, installation.RelativePath);
        Directory.CreateDirectory(editorPath);
        var cancellation = new CancellationTokenSource();

        SetupInstallationsSequence([version], []);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, new[] { version }, false))
            .Returns([version]);
        _mockHostSystem.Setup(x => x.DirectoryExists(editorPath))
            .Returns(() =>
            {
                cancellation.Cancel();
                return new Result<bool, FileOperationError>.Success(true);
            });

        await _removeCommand.Remove(cancellationToken: cancellation.Token, query: query);

        Assert.False(Directory.Exists(editorPath));
        Assert.Empty(Tombstones());
        _mockInstallationRegistry.Verify(x => x.Remove(installation.Key), Times.Once);
    }

    [Fact]
    public async Task Remove_StagesTheDirectoryBeforeUpdatingTheRegistry()
    {
        var version = "4.3.0-stable-standard";
        var query = new[] { "4.3" };
        var installation = CreateInstallation(version);
        var editorPath = Path.Combine(_root, installation.RelativePath);
        Directory.CreateDirectory(editorPath);
        var existedWhenRegistryWasUpdated = true;

        SetupInstallationsSequence([version], []);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, new[] { version }, false))
            .Returns([version]);
        _mockHostSystem.Setup(x => x.DirectoryExists(editorPath))
            .Returns(new Result<bool, FileOperationError>.Success(true));
        _mockInstallationRegistry.Setup(x => x.Remove(installation.Key))
            .Returns(() =>
            {
                existedWhenRegistryWasUpdated = Directory.Exists(editorPath);
                return new Result<Unit, InstallationRegistryError>.Success(Unit.Value);
            });

        await _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

        // Stage the directory before mutating the scan-derived registry document.
        Assert.False(existedWhenRegistryWasUpdated);
        Assert.False(Directory.Exists(editorPath));
        Assert.Empty(Tombstones());
    }

    [Fact]
    public async Task Remove_WhenRegistryUpdateFailsAfterStaging_LeavesNoHalfDeletedInstallation()
    {
        var version = "4.3.0-stable-standard";
        var query = new[] { "4.3" };
        var installation = CreateInstallation(version);
        var editorPath = Path.Combine(_root, installation.RelativePath);
        Directory.CreateDirectory(editorPath);

        SetupInstallations([version]);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, new[] { version }, false))
            .Returns([version]);
        _mockHostSystem.Setup(x => x.DirectoryExists(editorPath))
            .Returns(new Result<bool, FileOperationError>.Success(true));
        _mockInstallationRegistry.Setup(x => x.Remove(installation.Key))
            .Returns(new Result<Unit, InstallationRegistryError>.Failure(
                new InstallationRegistryError.NotFound(installation.Key)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query));

        // Gone, not half-deleted; the leftover is a tombstone the sweep collects
        Assert.False(Directory.Exists(editorPath));
        Assert.Single(Tombstones());
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

        await _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

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

        await _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

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

        await removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

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

        await removeCommand.Remove(cancellationToken: CancellationToken.None, query: emptyQuery);

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

        await removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

        // Should not remove symlinks since there are still installations
        _mockHostSystem.Verify(x => x.RemoveSymbolicLinks(), Times.Never);

        // Read once for the whole removal, not once per selected version
        _mockInstallationRegistry.Verify(x => x.GetDefault(), Times.Once);
    }

    // A non-interactive console cannot show the selection prompt, so an ambiguous query has to fail with a message
    // the user can act on rather than the prompt's own "terminal isn't interactive" crash.
    [Fact]
    public async Task Remove_WithAmbiguousQueryOnANonInteractiveConsole_ThrowsWithAnActionableMessage()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.3.0-mono", "4.2.0-stable" };
        var query = new[] { "4.3.0" };
        var filteredVersions = new[] { "4.3.0-stable", "4.3.0-mono" };

        SetupInstallations(installedVersions);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(filteredVersions);

        // Left non-interactive: TestConsole defaults to it, which is what a pipe or CI job looks like.
        var testConsole = new TestConsole();
        var removeCommand = CreateRemoveCommandWithConsole(testConsole);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            removeCommand.Remove(cancellationToken: CancellationToken.None, query: query));

        Assert.Contains("4.3.0-stable", exception.Message, StringComparison.Ordinal);
        Assert.Contains("4.3.0-mono", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not interactive", exception.Message, StringComparison.Ordinal);
        // The generic failure notice would bury the actionable message above.
        Assert.DoesNotContain("Something went wrong", testConsole.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remove_WithASingleMatchOnANonInteractiveConsole_StillRemoves()
    {
        var installedVersions = new[] { "4.3.0-stable", "4.2.0-stable" };
        var query = new[] { "4.3.0" };

        SetupInstallationsSequence(installedVersions, ["4.2.0-stable"]);
        _mockReleaseManager.Setup(x => x.FilterReleasesByQuery(query, installedVersions, false))
            .Returns(["4.3.0-stable"]);

        var testConsole = new TestConsole();
        var removeCommand = CreateRemoveCommandWithConsole(testConsole);

        await removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

        _mockInstallationRegistry.Verify(x => x.Remove(It.IsAny<string>()), Times.Once);
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
            _removeCommand.Remove(cancellationToken: cancellationToken, query: query));
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
            _removeCommand.Remove(cancellationToken: cancellationToken, query: query));
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

        await removeCommand.Remove(cancellationToken: CancellationToken.None, query: query);

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
            _removeCommand.Remove(cancellationToken: CancellationToken.None, query: query));
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

    private static TemplateInstallation CreateTemplateInstallation(string releaseName, string path) =>
        new("4.3.stable", releaseName, RuntimeEnvironment.Standard, path, null);

    private RemovalService CreateRemovalService(TestConsole console)
    {
        var hostSystem = new HostSystem(new SystemInfo(), _mockPathService.Object, NullLogger<HostSystem>.Instance);
        return new RemovalService(
            new DirectoryRemoval(hostSystem, NullLogger<DirectoryRemoval>.Instance, TimeProvider.System),
            hostSystem,
            _mockPathService.Object,
            _mockGodotPathService.Object,
            console,
            NullLogger<RemovalService>.Instance);
    }

    private string[] Tombstones() =>
        Directory.GetDirectories(_root, ".fgvm-removing-*", SearchOption.AllDirectories);

    private void SetupTemplateFor(string releaseName, string path) =>
        _mockTemplateRegistry.Setup(x => x.FindByReleaseName(releaseName))
            .Returns(new Result<TemplateInstallation, TemplateRegistryError>.Success(
                CreateTemplateInstallation(releaseName, path)));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
