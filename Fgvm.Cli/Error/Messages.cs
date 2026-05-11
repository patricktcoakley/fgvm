using Fgvm.Environment;
using Fgvm.Types;

namespace Fgvm.Cli.Error;

/// <summary>
///     CLI-specific user-facing messages with markup formatting
/// </summary>
public static class Messages
{
    public const string DefaultInstallationMarkerMarkup = ":eight_pointed_star:";
    public const string DefaultInstallationMarkerGlyph = "✴";
    public const string NonDefaultInstallationIndent = "   ";

    // Prompts
    public static string TypeToSearch => "[aqua]Type to search...[/]";
    public static string SelectARuntime => "[green]Standard or Mono (.NET Support)?[/]\n[hotpink_1](Press CTRL+C to cancel)[/]";
    public static string UnexpectedError => "[red]Unexpected error: version resolution succeeded but result is not success.[/]";
    public static string UnknownResolutionError => "[red]Unknown error occurred while resolving version.[/]";
    public static string UnknownSymlinkError => "[red]Unknown error occurred while checking current version[/]";
    public static string MoreChoicesText => "[grey](Move up and down to see more versions)[/]";

    // Symlinks
    public static string NoCurrentVersionSet => "[red]No current Godot version set.[/]";
    public static string DeveloperModeRequiredForSymlink =>
        "[yellow]Windows Developer Mode or elevated privileges are required to create symlinks.[/]\n" +
        "[dim]`fgvm godot` uses the symlink to launch Godot by default. You can also use `fgvm local` to create a `.fgvm-version` file.[/]\n" +
        "[dim]Enable Developer Mode or run as Administrator: https://learn.microsoft.com/en-us/windows/apps/get-started/enable-your-device-for-development[/]";

    public static string DeveloperModeRequiredForGodot =>
        "[red]No default symlink is available and Windows Developer Mode appears to be disabled.[/]\n" +
        "[dim]Run `fgvm local` to create a `.fgvm-version` file and run `fgvm godot` in the project folder, or enable Developer Mode or run as Administrator:[/]\n" +
        "[dim]https://learn.microsoft.com/en-us/windows/apps/get-started/enable-your-device-for-development[/]";

    public static string SymlinkPermissionDenied =>
        "[yellow]Unable to create symlinks due to insufficient permissions.[/]\n" +
        "[dim]You can still use `fgvm local` to create a `.fgvm-version` file, or adjust permissions and try again.[/]";
    public static string NoInstallationsToRemove => "[orange1] No installations available to remove. [/]";
    public static string SetAsDefaultVersionNote => "[dim]Set as default version.[/]";
    public static string AutoSetAsDefaultNote => "[dim]Set as default version since no other versions are installed.[/]";
    public static string InstallationSucceededButNotFound => "[red]Installation succeeded but version not found in installed list.[/]";
    public static string ErrorResolvingVersion => "[red]Error resolving Godot version for launch.[/]";

    // Version management
    public static string NoVersionsInstalled => "[red]No Godot versions installed.[/]";
    public static string CreatedVersionFile => "[dim]Created `.fgvm-version` file in current directory.[/]";
    public static string UpdatedVersionFile => "[dim]Updated `.fgvm-version` file in current directory.[/]";
    public static string ChooseFromInstalled => "[dim]Choose from installed versions:[/]";

    // List
    public static string ListPanelHeader => "List Of Installed Versions";

    // Search
    public static string AvailableVersionsHeader => "[green]List Of Available Versions[/]";

    // Exceptions
    public static string NoInstallationsFound => "No installations found. Please install one or more versions first.";
    public static string InvalidGodotVersion => "Invalid Godot version.";
    public static string NoInstallationsAndNoVersionFile => "No installations found and no `.fgvm-version` file. Install a version first with: fgvm install <version>";
    public static string NoVersionsInstalledPrompt => "No versions installed. Install a version first with: `fgvm install <version>`";
    public static string InstallationFailedNoVersions => "Installation failed and no versions available.";
    public static string UnknownInstallationOutcome => "Unknown installation outcome";
    public static string UnknownInstallationResultType => "Unknown installation result type";

    public static string SymlinkUnsupportedOS(string os) =>
        $"[yellow]Symlinks are not supported on this OS: {os}.[/]\n" +
        "[dim]You can still use `fgvm local` to create a `.fgvm-version` file.[/]";

    public static string CurrentVersionSetTo(string symlinkPath) => $"[green]Current version set to:[/] {symlinkPath}";
    public static string CurrentMacOSAppSetTo(string macAppSymlinkPath) => $"\n[green]Current macOS App set to:[/] {macAppSymlinkPath}";
    public static string ConfigurationError(string message) => $"[red]Configuration error: {message}[/]";
    public static string ExceptionMessage(string message) => $"[red]{message}.[/]";
    public static string SelectAVersionTo(string what) => $"[green]Select a version to {what}[/]\n[hotpink_1](Press CTRL+C to cancel)[/]";
    public static string SelectVersionsTo(string what) => $"[green]Select the versions to {what}[/]\n[hotpink_1](Press CTRL+C to cancel)[/]";

    // Cancellation
    public static string UserCancelled(string what) => $"[red]User cancelled {what} operation :prohibited:[/]";

    // Errors
    public static string SomethingWentWrong(string when, string logPath) =>
        $"[red]Something went wrong {when} 💣[/]\n[red]Please use [hotpink_1]fgvm logs[/] for more information or check {logPath}.[/]";

    public static string SomethingWentWrong(string when, IPathService pathService) =>
        SomethingWentWrong(when, pathService.LogPath);

    public static string SomethingWentWrong(string when) =>
        $"[red]Something went wrong {when} 💣[/]\n[red]Please use [hotpink_1]fgvm logs[/] for more information.[/]";


    public static string InvalidSymlinkWarn(string symlinkPath) =>
        $"[orange1]WARN: Symlink for {symlinkPath} was created but appears to be invalid. Removing it.[/]";

    // Installation
    public static string AlreadyInstalled(string releaseNameWithRuntime) => $"[yellow]{releaseNameWithRuntime} is already installed[/]";

    public static string InstallationNotFound(string version, IHostSystem hostSystem) =>
        $"[red]Version {version} could not be found for {hostSystem.SystemInfo.CurrentOS.ToDisplayString()} {hostSystem.SystemInfo.CurrentArch.ToDisplayString()}[/]";

    public static string InstallationFailed(string reason) => $"[red]Installation failed: {reason}[/]";
    public static string InstallationSuccessBase(string releaseNameWithRuntime) => $"Finished installing {releaseNameWithRuntime}! :party_popper:";
    public static string SuccessfullyInstalled(string releaseNameWithRuntime) => $"[green]Successfully installed {releaseNameWithRuntime}.[/]";

    // Version resolution
    public static string VersionResolutionNotFound(string version, IHostSystem hostSystem) =>
        $"[red]Version {version} could not be found for {hostSystem.SystemInfo.CurrentOS.ToDisplayString()} {hostSystem.SystemInfo.CurrentArch.ToDisplayString()}[/]";

    public static string VersionResolutionFailed(string reason) => $"[red]Version resolution failed: {reason}[/]";
    public static string InvalidVersion(string version) => $"[red]The version '{version}' is invalid[/]";
    public static string InvalidProjectVersion(string compatibleVersion) => $"[red]Invalid project version: {compatibleVersion}[/]";
    public static string SuccessfullySetVersion(string releaseNameWithRuntime) => $"[green]Successfully set version to {releaseNameWithRuntime}. [/]";
    public static string SetLocalVersion(string releaseNameWithRuntime) => $"[green]Set local version to {releaseNameWithRuntime}.[/]";

    public static string ProjectSpecifiesVersion(string projectVersion, string runtimeDisplaySuffix) =>
        $"[yellow]Project specifies {projectVersion}{runtimeDisplaySuffix}.[/]";

    public static string ProjectVersionNotInstalled(string projectVersion, string runtimeDisplaySuffix) =>
        $"[yellow]Project specifies {projectVersion}{runtimeDisplaySuffix} but it's not installed.[/]";

    public static string InstallingProjectVersion(string projectVersion, string runtimeDisplaySuffix) =>
        $"[dim]Installing {projectVersion}{runtimeDisplaySuffix}...[/]";

    public static string FailedToInstallProjectVersion(string projectVersion, string runtimeDisplaySuffix) =>
        $"[red]Failed to install {projectVersion}{runtimeDisplaySuffix}.[/]";

    public static string InstallationInstructions(string projectVersion, bool isDotNet) =>
        $"[dim]Run 'fgvm install {BuildInstallQuery(projectVersion, isDotNet)}' or 'fgvm local {BuildInstallQuery(projectVersion, isDotNet)}' to install it.[/]";

    public static string ManualInstallInstructions(string projectVersion, bool isDotNet) =>
        $"[dim]You can manually install with: fgvm install {BuildInstallQuery(projectVersion, isDotNet)}[/]";

    public static string SuccessfullyInstalledAndUsing(string projectVersion, string runtimeDisplaySuffix, string newCompatibleVersion) =>
        $"[green]Successfully installed and using: {projectVersion}{runtimeDisplaySuffix} → {newCompatibleVersion}[/]";

    public static string UsingProjectVersion(string projectVersion, string runtimeDisplaySuffix, string compatibleVersion) =>
        $"[dim]Using project version: {projectVersion}{runtimeDisplaySuffix} → {compatibleVersion}[/]";

    public static string NoInstalledVersionMatching(string query) => $"[yellow]No installed version found matching '{query}'.[/]";
    public static string Installing(string query) => $"[dim]Installing {query}...[/]";
    public static string FailedToInstallMatching(string query) => $"[red]Failed to install version matching '{query}'.[/]";

    public static string InstallingAutoDetected(string projectVersion, string runtimeDisplaySuffix) =>
        $"[dim]Installing {projectVersion}...{runtimeDisplaySuffix}[/]";

    // Project confirmation
    public static string ProjectRequiresInstall(string projectVersion, string runtimeText) =>
        $"[yellow]Project requires {projectVersion}{runtimeText} but it's not installed.[/]\n[green]Would you like to install it now?[/]";

    public static string AutoDetectedProject(string fileName) => $"[dim]Auto-detected project file: {fileName}[/]";
    public static string LaunchedGodotDetached(string versionName, int processId) => $"[green]Launched Godot {versionName} in detached mode (PID: {processId}).[/]";

    public static string RunningAttachedMode(string versionName) =>
        $"[yellow]Note: Running Godot {versionName} in attached mode due to arguments requiring terminal output.[/]";

    // Remove
    public static string FoundExactMatch(string versionToRemove) => $"[yellow]Found exactly one version matching your query: {versionToRemove}[/]";
    public static string NoVersionsMatchingQuery(string queryJoin) => $"[orange1]Couldn't find any versions with query `{queryJoin}`. Please try again. [/]";
    public static string SuccessfullyRemoved(string selectionPath) => $"Successfully removed {selectionPath} :wastebasket: ";
    public static string LogLevelOutOfRange(string level) => $"{level} is not valid. Accepted values include: default, debug, info, warning, error, or critical";
    public static string LogPathNotFound(string logPath) => $"Path to logs {logPath} doesn't exist.";
    public static string UnableToGetRelease(string version) => $"Unable to get release with selection `{version}`.";

    // Checksum verification
    public static string ChecksumUnavailable(string releaseNameWithRuntime) =>
        $"[orange1]Warning: Checksum unavailable for {releaseNameWithRuntime}. Installation continued without verification.[/]";

    public static string ChecksumMismatch(string fileName, string expected, string actual) =>
        $"[red]Checksum mismatch for {fileName}![/]\n[red]Expected: {expected}[/]\n[red]Actual:   {actual}[/]\n[red]This could indicate a corrupted download or security issue.[/]";

    private static string BuildInstallQuery(string projectVersion, bool isDotNet)
    {
        var hasRuntimeSuffix = projectVersion.EndsWith("-mono", StringComparison.OrdinalIgnoreCase) ||
                               projectVersion.EndsWith("-standard", StringComparison.OrdinalIgnoreCase);

        if (hasRuntimeSuffix)
        {
            return projectVersion;
        }

        return isDotNet ? $"{projectVersion} mono" : projectVersion;
    }
}
