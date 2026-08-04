using Fgvm.Environment;

namespace Fgvm.Tests.Environment;

public sealed class StagedDirectoryNamesTests
{
    [Fact]
    public void CreateTombstonePath_IsRecognisedAsATombstone()
    {
        var path = StagedDirectoryNames.CreateTombstonePath(Path.Combine("root", "installations", "4.3-stable"));

        Assert.True(StagedDirectoryNames.TryClassify(Path.GetFileName(path), out var kind));
        Assert.Equal(StagedDirectoryKind.Tombstone, kind);
    }

    [Fact]
    public void CreateBackupPath_IsRecognisedAsABackup()
    {
        var path = StagedDirectoryNames.CreateBackupPath(Path.Combine("root", "installations", "4.3-stable"));

        Assert.True(StagedDirectoryNames.TryClassify(Path.GetFileName(path), out var kind));
        Assert.Equal(StagedDirectoryKind.Backup, kind);
    }

    [Fact]
    public void CreateInstallStagingPath_IsRecognisedAsStaging()
    {
        var path = StagedDirectoryNames.CreateInstallStagingPath("root");

        Assert.True(StagedDirectoryNames.TryClassify(Path.GetFileName(path), out var kind));
        Assert.Equal(StagedDirectoryKind.Staging, kind);
    }

    [Fact]
    public void CreateTemplateStagingPath_IsRecognisedAsStaging()
    {
        var path = StagedDirectoryNames.CreateTemplateStagingPath("root");

        Assert.True(StagedDirectoryNames.TryClassify(Path.GetFileName(path), out var kind));
        Assert.Equal(StagedDirectoryKind.Staging, kind);
    }

    [Fact]
    public void CreatedPathsStayBesideTheirTarget()
    {
        var target = Path.Combine("root", "installations", "4.3-stable");

        Assert.Equal(Path.Combine("root", "installations"), Path.GetDirectoryName(StagedDirectoryNames.CreateTombstonePath(target)));
        Assert.Equal(Path.Combine("root", "installations"), Path.GetDirectoryName(StagedDirectoryNames.CreateBackupPath(target)));
    }

    [Fact]
    public void CreatedNamesEndWithTheOriginalDirectoryName()
    {
        var target = Path.Combine("root", "installations", "4.3-stable");

        Assert.EndsWith("-4.3-stable", Path.GetFileName(StagedDirectoryNames.CreateTombstonePath(target)), StringComparison.Ordinal);
        Assert.EndsWith("-4.3-stable", Path.GetFileName(StagedDirectoryNames.CreateBackupPath(target)), StringComparison.Ordinal);
    }

    // A user's own dot-directory must never be mistaken for one of ours, since a sweep deletes what it recognises.
    [Theory]
    [InlineData("installations")]
    [InlineData(".backup-notes")]
    [InlineData(".backup-")]
    [InlineData(".fgvm-removing-")]
    [InlineData(".fgvm-staging-")]
    [InlineData(".fgvm-staging-not-a-guid")]
    [InlineData(".backup-0123456789abcdef-short")]
    [InlineData(".fgvm-removing-0123456789abcdef0123456789abcdef")]
    [InlineData("backup-0123456789abcdef0123456789abcdef-editor")]
    public void TryClassify_RejectsAnythingThatIsNotOurMarker(string directoryName)
    {
        Assert.False(StagedDirectoryNames.TryClassify(directoryName, out _));
    }

    [Fact]
    public void TryClassify_RejectsATombstoneWithNoTrailingName()
    {
        Assert.False(StagedDirectoryNames.TryClassify($".fgvm-removing-{Guid.NewGuid():N}", out _));
    }

    [Fact]
    public void TryClassify_RejectsStagingWithATrailingName()
    {
        Assert.False(StagedDirectoryNames.TryClassify($".fgvm-staging-{Guid.NewGuid():N}-editor", out _));
    }
}
