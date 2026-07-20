using System.IO.Compression;

namespace Fgvm.Extensions;

public static class ZipArchiveExtensions
{
    /// <summary>
    /// Extracts a Godot archive, flattening it one level when needed. Godot's Mono releases for Linux and Windows
    /// are not archived at the root level like the others, i.e. this extracts `/blahblah/file1.txt` to
    /// `/my/path/file1.txt` instead of `/my/path/blahblah/file1.txt`.
    /// </summary>
    /// <param name="archivePath">The path to the archive to extract.</param>
    /// <param name="extractPath">The directory receiving the extracted files.</param>
    /// <param name="overwrite">Whether existing files may be replaced.</param>
    /// <param name="cancellationToken">The token used to cancel extraction between entries.</param>
    /// <returns>A task that completes when all archive entries have been extracted.</returns>
    public static Task ExtractWithFlatteningSupportAsync(string archivePath,
        string extractPath,
        bool overwrite,
        CancellationToken cancellationToken = default
    ) => ExtractWithFlatteningSupportAsync(
        () => ParallelZipExtractor.OpenArchiveStream(archivePath),
        extractPath,
        overwrite,
        cancellationToken);

    /// <summary>
    /// Extracts an archive from independently opened streams using the Godot layout rules.
    /// </summary>
    /// <param name="openArchive">A factory that returns a fresh readable, seekable stream for each open.</param>
    /// <param name="extractPath">The directory receiving the extracted files.</param>
    /// <param name="overwrite">Whether existing files may be replaced.</param>
    /// <param name="cancellationToken">The token used to cancel extraction between entries.</param>
    /// <returns>A task that completes when all archive entries have been extracted.</returns>
    internal static async Task ExtractWithFlatteningSupportAsync(Func<Stream> openArchive,
        string extractPath,
        bool overwrite,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(openArchive);
        await using var archiveStream = openArchive();
        await using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        // Handle macOS .app and Standard releases on other platforms since they are already flattened while Windows is not.
        var isAlreadyFlattened = archive.Entries.Any(entry =>
            entry.FullName.Split('/').Length == 1
            || entry.FullName.EndsWith(".app/"));

        var workItems = archive.Entries
            .Select((entry, index) => (entry, index))
            .Where(item => isAlreadyFlattened || !string.IsNullOrEmpty(item.entry.Name))
            .Select(item => new ZipExtractionWorkItem(
                item.index,
                isAlreadyFlattened
                    ? item.entry.FullName.Replace('/', Path.DirectorySeparatorChar)
                    : string.Join(Path.DirectorySeparatorChar, item.entry.FullName.Split('/').Skip(1)),
                string.IsNullOrEmpty(item.entry.Name),
                false))
            .ToArray(); // Must materialize before `archive` is disposed below; entry.Open() would fail on a lazy query.

        await ParallelZipExtractor.ExtractAsync(
            openArchive,
            extractPath,
            workItems,
            overwrite,
            cancellationToken);
    }
}
