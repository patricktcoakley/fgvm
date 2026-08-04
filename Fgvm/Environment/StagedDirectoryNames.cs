namespace Fgvm.Environment;

/// <summary>
///     What a marker directory left on disk represents, which decides whether a sweep may delete it.
/// </summary>
public enum StagedDirectoryKind
{
    /// <summary>A directory renamed aside for deletion. The content is already doomed.</summary>
    Tombstone,

    /// <summary>A commit's rollback copy of a destination it is replacing.</summary>
    Backup,

    /// <summary>A partially populated directory an install or template extraction is filling.</summary>
    Staging
}

/// <summary>
///     Creates and recognises the marker directories fgvm leaves beside a destination. Every prefix lives here so
///     that adding one cannot silently escape <see cref="IDirectoryRemoval.Sweep" />, which is how orphaned staging
///     and backup directories previously accumulated unnoticed.
/// </summary>
public static class StagedDirectoryNames
{
    private const string TombstonePrefix = ".fgvm-removing-";
    private const string BackupPrefix = ".backup-";
    private const string InstallStagingPrefix = ".fgvm-staging-";
    private const string TemplateStagingPrefix = ".fgvm-template-staging-";
    private const int GuidLength = 32;

    public static string CreateTombstonePath(string directoryPath) =>
        CreateNamedMarker(TombstonePrefix, directoryPath);

    public static string CreateBackupPath(string destinationPath) =>
        CreateNamedMarker(BackupPrefix, destinationPath);

    public static string CreateInstallStagingPath(string parentPath) =>
        Path.Combine(parentPath, $"{InstallStagingPrefix}{Guid.NewGuid():N}");

    public static string CreateTemplateStagingPath(string parentPath) =>
        Path.Combine(parentPath, $"{TemplateStagingPrefix}{Guid.NewGuid():N}");

    /// <summary>
    ///     Recognises a marker directory by name. Matching is strict so a directory that merely starts with one of
    ///     the prefixes, such as a user's own `.backup-notes`, is never mistaken for one of ours.
    /// </summary>
    /// <param name="directoryName">The directory name, without its parent path.</param>
    /// <param name="kind">What the marker represents when recognised.</param>
    /// <returns>Whether the name is a marker fgvm created.</returns>
    public static bool TryClassify(string directoryName, out StagedDirectoryKind kind)
    {
        if (HasGuidSuffix(directoryName, TemplateStagingPrefix) || HasGuidSuffix(directoryName, InstallStagingPrefix))
        {
            kind = StagedDirectoryKind.Staging;
            return true;
        }

        if (HasGuidAndNameSuffix(directoryName, TombstonePrefix))
        {
            kind = StagedDirectoryKind.Tombstone;
            return true;
        }

        if (HasGuidAndNameSuffix(directoryName, BackupPrefix))
        {
            kind = StagedDirectoryKind.Backup;
            return true;
        }

        kind = default;
        return false;
    }

    // Dot-prefixed so the registry scans skip a marker left by a hard kill rather than read it as an installation.
    private static string CreateNamedMarker(string prefix, string directoryPath)
    {
        var parentPath = Path.GetDirectoryName(directoryPath) ??
                         throw new InvalidOperationException($"Path `{directoryPath}` has no parent directory.");
        return Path.Combine(parentPath, $"{prefix}{Guid.NewGuid():N}-{Path.GetFileName(directoryPath)}");
    }

    private static bool HasGuidSuffix(string directoryName, string prefix) =>
        directoryName.StartsWith(prefix, StringComparison.Ordinal) &&
        IsGuid(directoryName.AsSpan(prefix.Length));

    private static bool HasGuidAndNameSuffix(string directoryName, string prefix)
    {
        if (!directoryName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var marker = directoryName.AsSpan(prefix.Length);
        return marker.Length > GuidLength + 1 &&
               marker[GuidLength] == '-' &&
               IsGuid(marker[..GuidLength]);
    }

    private static bool IsGuid(ReadOnlySpan<char> value) =>
        value.Length == GuidLength && Guid.TryParseExact(value, "N", out _);
}
