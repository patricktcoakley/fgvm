namespace Fgvm.Godot.Export;

internal enum ExportPlatformShape
{
    ContainingDirectory,
    DestinationDirectory,
    File
}

internal static class ExportPlatform
{
    private const string Windows = "Windows Desktop";
    private const string Linux = "Linux";
    private const string LinuxX11 = "Linux/X11";
    private const string MacOS = "macOS";
    private const string Web = "Web";
    private const string Android = "Android";
    private const string IOS = "iOS";

    internal static string? DefaultFileName(string platform, string slug) =>
        platform switch
        {
            Windows => slug + ".exe",
            Linux or LinuxX11 => slug,
            MacOS => slug + ".app",
            Web => "index.html",
            Android => slug + ".apk",
            IOS => slug + ".zip",
            _ => null
        };

    internal static ExportPlatformShape ArtifactShape(string platform, string destination) =>
        (platform, Path.GetExtension(destination).ToLowerInvariant()) switch
        {
            (MacOS, ".app") => ExportPlatformShape.DestinationDirectory,
            (MacOS, ".zip" or ".dmg" or ".pkg") => ExportPlatformShape.File,
            (Web or IOS, ".zip") => ExportPlatformShape.File,
            (Android, ".apk" or ".aab") => ExportPlatformShape.File,
            _ => ExportPlatformShape.ContainingDirectory
        };

    internal static ExportDestinationKind ExpectedDestinationKind(string platform, string destination) =>
        (platform, Path.GetExtension(destination).ToLowerInvariant()) switch
        {
            (MacOS, ".app") => ExportDestinationKind.Directory,
            (Windows or Linux or LinuxX11 or Web or Android or IOS, _) => ExportDestinationKind.File,
            _ => ExportDestinationKind.Either
        };
}
