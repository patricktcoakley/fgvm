using System.IO.Compression;

namespace Fgvm.Extensions;

/// <summary>
/// Describes one archive entry and its resolved extraction policy.
/// </summary>
/// <param name="EntryIndex">The entry index in the archive.</param>
/// <param name="RelativePath">The destination path relative to the extraction root.</param>
/// <param name="IsDirectory">Whether the entry should create a directory instead of a file.</param>
/// <param name="ApplyUnixPermissions">Whether Unix permission bits should be applied after extraction.</param>
internal readonly record struct ZipExtractionWorkItem(
    int EntryIndex,
    string RelativePath,
    bool IsDirectory,
    bool ApplyUnixPermissions
);

/// <summary>
/// Extracts planned ZIP entries concurrently using independent archive streams.
/// </summary>
internal static class ParallelZipExtractor
{
    private const int ArchiveIoBufferSize = checked((int)ByteSize.Mebibyte);
    private const int UnixModeShift = 16;

    private const UnixFileMode UnixPermissionMask = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>
    /// Opens a read-only stream over an archive file, configured for asynchronous sequential reads.
    /// </summary>
    /// <param name="archivePath">The path to the archive.</param>
    /// <returns>The opened stream.</returns>
    internal static Stream OpenArchiveStream(string archivePath) => new FileStream(
        archivePath,
        FileMode.Open,
        FileAccess.Read,
        FileShare.Read,
        ArchiveIoBufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    /// <summary>
    /// Extracts planned entries from a file-backed archive.
    /// </summary>
    /// <param name="archivePath">The path to the archive.</param>
    /// <param name="extractPath">The extraction root.</param>
    /// <param name="workItems">The validated entry extraction plans.</param>
    /// <param name="overwrite">Whether existing files may be replaced.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="maxWorkers">An optional worker limit; omitted means the processor count.</param>
    /// <returns>A task that completes after extraction.</returns>
    public static Task ExtractAsync(string archivePath,
        string extractPath,
        IReadOnlyList<ZipExtractionWorkItem> workItems,
        bool overwrite,
        CancellationToken cancellationToken,
        int? maxWorkers = null
    ) => ExtractAsync(
        () => OpenArchiveStream(archivePath),
        extractPath,
        workItems,
        overwrite,
        cancellationToken,
        maxWorkers);

    /// <summary>
    /// Extracts planned entries using a fresh stream for each concurrent archive open.
    /// </summary>
    /// <param name="openArchive">A factory that returns a fresh readable, seekable stream.</param>
    /// <param name="extractPath">The extraction root.</param>
    /// <param name="workItems">The validated entry extraction plans.</param>
    /// <param name="overwrite">Whether existing files may be replaced.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="maxWorkers">An optional concurrency limit; omitted means the processor count.</param>
    /// <returns>A task that completes after extraction.</returns>
    internal static async Task ExtractAsync(Func<Stream> openArchive,
        string extractPath,
        IReadOnlyList<ZipExtractionWorkItem> workItems,
        bool overwrite,
        CancellationToken cancellationToken,
        int? maxWorkers = null
    )
    {
        ArgumentNullException.ThrowIfNull(openArchive);

        if (workItems.Count == 0)
        {
            return;
        }

        // Group by destination to avoid collisions
        var workGroups = workItems
            .Select(workItem => new ResolvedZipExtractionWorkItem(
                workItem.EntryIndex,
                GetSafeDestinationPath(extractPath, workItem.RelativePath),
                workItem.IsDirectory,
                workItem.ApplyUnixPermissions))
            .GroupBy(workItem => workItem.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray())
            .ToArray();

        await Parallel.ForEachAsync(workGroups, new ParallelOptions
        {
            MaxDegreeOfParallelism = ResolveWorkerCount(workGroups.Length, maxWorkers),
            CancellationToken = cancellationToken
        }, async (group, token) =>
        {
            await using var archiveStream = openArchive();
            await using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
            foreach (var workItem in group)
            {
                await ExtractWorkItem(archive, workItem, overwrite, token);
            }
        });
    }

    private static async Task ExtractWorkItem(ZipArchive archive,
        ResolvedZipExtractionWorkItem workItem,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        if (workItem.IsDirectory)
        {
            Directory.CreateDirectory(workItem.DestinationPath);
            return;
        }

        var entry = archive.Entries[workItem.EntryIndex];
        var destinationDirectory = Path.GetDirectoryName(workItem.DestinationPath)
                                   ?? throw new InvalidDataException(
                                       $"Extraction destination has no parent directory: {workItem.DestinationPath}");
        Directory.CreateDirectory(destinationDirectory);
        await entry.ExtractToFileAsync(workItem.DestinationPath, overwrite, cancellationToken);
        if (workItem.ApplyUnixPermissions)
        {
            PreserveUnixPermissions(entry, workItem.DestinationPath);
        }
    }

    private static int ResolveWorkerCount(int workItemCount, int? maxWorkers)
    {
        var cpuLimit = Math.Max(1, System.Environment.ProcessorCount);
        var requestedLimit = maxWorkers.GetValueOrDefault(cpuLimit);
        var effectiveLimit = Math.Min(cpuLimit, Math.Max(1, requestedLimit));
        return Math.Min(workItemCount, effectiveLimit);
    }

    private static string GetSafeDestinationPath(string extractPath, string relativePath)
    {
        var destinationPath = Path.GetFullPath(Path.Combine(extractPath, relativePath));
        var rootPath = Path.GetFullPath(extractPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                        Path.DirectorySeparatorChar);

        if (!destinationPath.StartsWith(rootPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Archive entry is outside the target directory: {relativePath}");
        }

        return destinationPath;
    }

    private static void PreserveUnixPermissions(ZipArchiveEntry entry, string destinationPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var unixMode = (UnixFileMode)(entry.ExternalAttributes >> UnixModeShift & (int)UnixPermissionMask);
        if (unixMode != 0)
        {
            File.SetUnixFileMode(destinationPath, unixMode);
        }
    }

    private readonly record struct ResolvedZipExtractionWorkItem(
        int EntryIndex,
        string DestinationPath,
        bool IsDirectory,
        bool ApplyUnixPermissions
    );
}
