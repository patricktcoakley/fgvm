using System.IO.Compression;

namespace Fgvm.Extensions;

public static class ZipArchiveExtensions
{
    /// <summary>
    ///     Basically here for convenience to extract the Godot Mono releases for Linux and Windows one level higher since they
    ///     are not archived at the root-level like the others, i.e. extract `/blahblah/file1.txt` to `/my/path/file1.txt`
    ///     instead
    ///     of `/my/path/blahblah/file1.txt`.
    /// </summary>
    /// <param name="archive"></param>
    /// <param name="extractPath"></param>
    /// <param name="overwrite"></param>
    private static void ExtractFlattenedToDirectory(this ZipArchive archive, string extractPath, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(extractPath);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
            {
                continue;
            }

            var split = entry.FullName.Split('/');
            var flattenedPath = string.Join(Path.DirectorySeparatorChar, split.Skip(1));
            var destFullPath = Path.GetFullPath(Path.Combine(extractPath, flattenedPath));
            var extractFullPath = Path.GetFullPath(extractPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                                   Path.DirectorySeparatorChar);
            if (!destFullPath.StartsWith(extractFullPath, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Entry is outside the target dir: {destFullPath}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destFullPath)!);
            entry.ExtractToFile(destFullPath, overwrite);
        }
    }

    /// <summary>
    ///     A method here to just hide some of the mess with the different extraction methods based on Linux/Windows Mono vs
    ///     the others.
    /// </summary>
    /// <param name="archive"></param>
    /// <param name="extractPath"></param>
    /// <param name="overwrite"></param>
    public static void ExtractWithFlatteningSupport(this ZipArchive archive, string extractPath, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(extractPath);

        // Handle macOS .app and Standard releases on other platforms since they are already flattened while Windows is not
        var isAlreadyFlattened = archive.Entries.Any(entry =>
            entry.FullName.Split('/').Length == 1
            || entry.FullName.EndsWith(".app/"));

        if (isAlreadyFlattened)
        {
            archive.ExtractToDirectory(extractPath, overwrite);
        }
        else
        {
            archive.ExtractFlattenedToDirectory(extractPath, overwrite);
        }
    }
}
