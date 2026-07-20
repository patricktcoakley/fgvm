using System.IO.Compression;
using Fgvm.Extensions;
using static Fgvm.Tests.TestSupport.ZipArchiveTestBuilder;

namespace Fgvm.Tests.Extensions;

public sealed class ParallelZipExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fgvm-parallel-zip-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ExtractAsync_OpensAnIndependentStreamPerWorkItemGroup()
    {
        var archiveBytes = CreateArchive(("one.txt", "one"), ("nested/two.txt", "two"));
        var workItems = new ZipExtractionWorkItem[]
        {
            new(0, "one.txt", false, false),
            new(1, Path.Combine("nested", "two.txt"), false, false)
        };
        var openCount = 0;

        await ParallelZipExtractor.ExtractAsync(
            () =>
            {
                Interlocked.Increment(ref openCount);
                return new MemoryStream(archiveBytes, writable: false);
            },
            _root,
            workItems,
            overwrite: false,
            CancellationToken.None,
            maxWorkers: 2);

        // No two entries share a destination, so each becomes its own group and gets its own archive open.
        Assert.Equal(2, openCount);
        Assert.Equal("one", await File.ReadAllTextAsync(Path.Combine(_root, "one.txt")));
        Assert.Equal("two", await File.ReadAllTextAsync(Path.Combine(_root, "nested", "two.txt")));
    }

    [Fact]
    public async Task ExtractAsync_ExtractsTpzShapedArchiveFromLargeMemoryStream()
    {
        const int payloadSize = 1024 * 1024;
        var entryNames = new[]
        {
            "templates/version.txt",
            "templates/android_source.zip",
            "templates/ios.zip",
            "templates/macos.zip",
            "templates/android_debug.apk",
            "templates/android_release.apk",
            "templates/windows_debug_x86_32.exe",
            "templates/windows_release_x86_32.exe",
            "templates/windows_debug_x86_64.exe",
            "templates/windows_release_x86_64.exe",
            "templates/windows_debug_arm64.exe",
            "templates/windows_release_arm64.exe",
            "templates/linux_debug.x86_32",
            "templates/linux_release.x86_32",
            "templates/linux_debug.x86_64",
            "templates/linux_release.x86_64",
            "templates/linux_debug.arm64",
            "templates/linux_release.arm64",
            "templates/linux_debug.arm32",
            "templates/linux_release.arm32",
            "templates/web_debug.zip",
            "templates/web_release.zip",
            "templates/web_nothreads_debug.zip",
            "templates/web_nothreads_release.zip",
            "templates/web_dlink_debug.zip",
            "templates/web_dlink_release.zip",
            "templates/web_dlink_nothreads_debug.zip",
            "templates/web_dlink_nothreads_release.zip",
            "templates/icudt_godot.dat",
            "templates/windows_debug_x86_32_console.exe",
            "templates/windows_release_x86_32_console.exe",
            "templates/windows_debug_x86_64_console.exe",
            "templates/windows_release_x86_64_console.exe",
            "templates/windows_debug_arm64_console.exe",
            "templates/windows_release_arm64_console.exe"
        };
        var archiveBytes = CreateTpzShapedArchive(entryNames, payloadSize);
        var workItems = entryNames
            .Select((name, index) => new ZipExtractionWorkItem(index, name, false, false))
            .ToArray();
        var openCount = 0;

        await ParallelZipExtractor.ExtractAsync(
            () =>
            {
                Interlocked.Increment(ref openCount);
                return new MemoryStream(archiveBytes, writable: false);
            },
            _root,
            workItems,
            overwrite: false,
            CancellationToken.None,
            maxWorkers: 4);

        // maxWorkers caps concurrency, not archive-open count: each non-colliding entry is its own group/open.
        Assert.Equal(entryNames.Length, openCount);
        Assert.Equal(entryNames.Length, Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Count());
        Assert.Equal("4.7.stable", await File.ReadAllTextAsync(Path.Combine(_root, "templates", "version.txt")));
        foreach (var name in entryNames.Skip(1))
        {
            Assert.Equal(payloadSize, new FileInfo(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar))).Length);
        }
    }

    [Fact]
    public async Task ExtractAsync_ExtractsLargeTpzPayloadFromMemoryStream()
    {
        const int payloadSize = 8 * 1024 * 1024;
        var entryNames = Enumerable.Range(0, 32)
            .Select(index => $"templates/platform-{index:D2}.zip")
            .ToArray();
        var archive = CreateLargeTpzArchive(entryNames, payloadSize);
        var workItems = entryNames
            .Select((name, index) => new ZipExtractionWorkItem(index, name, false, false))
            .ToArray();
        var openCount = 0;

        await ParallelZipExtractor.ExtractAsync(
            () =>
            {
                Interlocked.Increment(ref openCount);
                return new MemoryStream(archive.Buffer, 0, archive.Length, writable: false);
            },
            _root,
            workItems,
            overwrite: false,
            CancellationToken.None);

        // One open per group (no collisions here, so per entry), independent of processor count or concurrency cap.
        Assert.Equal(entryNames.Length, openCount);
        foreach (var name in entryNames)
        {
            Assert.Equal(payloadSize, new FileInfo(Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar))).Length);
        }
    }

    [Fact]
    public async Task ExtractAsync_RejectsUnsafeDestinationBeforeOpeningWorkers()
    {
        var archiveBytes = CreateArchive(("safe.txt", "safe"));
        var workItems = new[] { new ZipExtractionWorkItem(0, "../outside.txt", false, false) };
        var openCount = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => ParallelZipExtractor.ExtractAsync(
            () =>
            {
                Interlocked.Increment(ref openCount);
                return new MemoryStream(archiveBytes, writable: false);
            },
            _root,
            workItems,
            overwrite: false,
            CancellationToken.None));

        Assert.Equal(0, openCount);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
    }

    [Fact]
    public async Task ExtractAsync_ReturnsWithoutOpeningArchive_WhenWorkItemsIsEmpty()
    {
        var openCount = 0;

        await ParallelZipExtractor.ExtractAsync(
            () =>
            {
                Interlocked.Increment(ref openCount);
                return new MemoryStream();
            },
            _root,
            [],
            overwrite: false,
            CancellationToken.None);

        Assert.Equal(0, openCount);
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task ExtractAsync_CreatesDirectory_ForDirectoryWorkItem_WithoutDereferencingArchiveEntries()
    {
        var archiveBytes = CreateArchive();
        var workItems = new[] { new ZipExtractionWorkItem(999, Path.Combine("nested", "emptydir"), IsDirectory: true, ApplyUnixPermissions: false) };

        await ParallelZipExtractor.ExtractAsync(
            () => new MemoryStream(archiveBytes, writable: false),
            _root,
            workItems,
            overwrite: false,
            CancellationToken.None);

        var directoryPath = Path.Combine(_root, "nested", "emptydir");
        Assert.True(Directory.Exists(directoryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directoryPath));
    }

    [Fact]
    public async Task ExtractWithFlatteningSupportAsync_StripsSingleWrapperFromMemoryArchive()
    {
        var archiveBytes = CreateArchive(("package/addons/example/plugin.cfg", "[plugin]"));

        await ZipArchiveExtensions.ExtractWithFlatteningSupportAsync(
            () => new MemoryStream(archiveBytes, writable: false),
            _root,
            overwrite: false);

        Assert.Equal("[plugin]", await File.ReadAllTextAsync(Path.Combine(_root, "addons", "example", "plugin.cfg")));
    }

    [Fact]
    public async Task ExtractAsync_SerializesEntriesWhoseDestinationsCollideByCase()
    {
        const int pairCount = 40;
        var files = Enumerable.Range(0, pairCount)
            .SelectMany(index => new (string Name, string Contents)[]
            {
                ($"data/file{index:D2}.bin", $"first-{index}"),
                ($"data/FILE{index:D2}.bin", $"second-{index}")
            })
            .ToArray();
        var archiveBytes = CreateArchive(files);
        var workItems = files
            .Select((file, index) => new ZipExtractionWorkItem(index, file.Name.Replace('/', Path.DirectorySeparatorChar), false, false))
            .ToArray();

        await ParallelZipExtractor.ExtractAsync(
            () => new MemoryStream(archiveBytes, writable: false),
            _root,
            workItems,
            overwrite: true,
            CancellationToken.None,
            maxWorkers: 4);

        for (var index = 0; index < pairCount; index++)
        {
            var lowerPath = Path.Combine(_root, "data", $"file{index:D2}.bin");
            var upperPath = Path.Combine(_root, "data", $"FILE{index:D2}.bin");
            if (FileSystemIsCaseInsensitive())
            {
                // Colliding entries must resolve like sequential extraction: the later archive entry wins.
                Assert.Equal($"second-{index}", await File.ReadAllTextAsync(lowerPath));
            }
            else
            {
                Assert.Equal($"first-{index}", await File.ReadAllTextAsync(lowerPath));
                Assert.Equal($"second-{index}", await File.ReadAllTextAsync(upperPath));
            }
        }
    }

    [Fact]
    public async Task ExtractAsync_ThrowsOperationCanceled_WithoutExtracting_WhenAlreadyCancelled()
    {
        var archiveBytes = CreateArchive(("one.txt", "one"));
        var workItems = new[] { new ZipExtractionWorkItem(0, "one.txt", false, false) };
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParallelZipExtractor.ExtractAsync(
            () => new MemoryStream(archiveBytes, writable: false),
            _root,
            workItems,
            overwrite: false,
            cancellation.Token));

        Assert.False(File.Exists(Path.Combine(_root, "one.txt")));
    }

    [Fact]
    public async Task ExtractAsync_PropagatesEntryFailure_AcrossWorkers()
    {
        var files = Enumerable.Range(0, 64)
            .Select(index => ($"file{index:D2}.txt", $"content-{index}"))
            .ToArray();
        var archiveBytes = CreateArchive(files);
        var workItems = files
            .Select((file, index) => new ZipExtractionWorkItem(index, file.Item1, false, false))
            .ToArray();
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "file32.txt"), "already-here");

        // overwrite: false makes the pre-existing file fail its work item; the failure must surface, not hang.
        await Assert.ThrowsAsync<IOException>(() => ParallelZipExtractor.ExtractAsync(
            () => new MemoryStream(archiveBytes, writable: false),
            _root,
            workItems,
            overwrite: false,
            CancellationToken.None,
            maxWorkers: 4));

        Assert.Equal("already-here", await File.ReadAllTextAsync(Path.Combine(_root, "file32.txt")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private bool FileSystemIsCaseInsensitive()
    {
        Directory.CreateDirectory(_root);
        var probe = Path.Combine(_root, "case-probe");
        File.WriteAllText(probe, "");
        return File.Exists(Path.Combine(_root, "CASE-PROBE"));
    }

    private static byte[] CreateTpzShapedArchive(string[] entryNames, int payloadSize)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < entryNames.Length; index++)
            {
                var entry = archive.CreateEntry(entryNames[index], CompressionLevel.Fastest);
                using var output = entry.Open();
                if (index == 0)
                {
                    using var version = new StreamWriter(output, leaveOpen: true);
                    version.Write("4.7.stable");
                    continue;
                }

                var payload = new byte[payloadSize];
                var state = (uint)(index + 1);
                for (var offset = 0; offset < payload.Length; offset++)
                {
                    state ^= state << 13;
                    state ^= state >> 17;
                    state ^= state << 5;
                    payload[offset] = (byte)state;
                }

                output.Write(payload);
            }
        }

        return stream.ToArray();
    }

    private sealed record InMemoryArchive(byte[] Buffer, int Length);

    private static InMemoryArchive CreateLargeTpzArchive(string[] entryNames, int payloadSize)
    {
        var stream = new MemoryStream(checked(payloadSize * entryNames.Length + 1024 * 1024));
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var buffer = new byte[1024 * 1024];
            var state = 0x9E3779B9u;
            foreach (var entryName in entryNames)
            {
                using var output = archive.CreateEntry(entryName, CompressionLevel.Fastest).Open();
                var remaining = payloadSize;
                while (remaining > 0)
                {
                    var count = Math.Min(remaining, buffer.Length);
                    for (var offset = 0; offset < count; offset++)
                    {
                        state ^= state << 13;
                        state ^= state >> 17;
                        state ^= state << 5;
                        buffer[offset] = (byte)state;
                    }

                    output.Write(buffer, 0, count);
                    remaining -= count;
                }
            }
        }

        return new InMemoryArchive(stream.GetBuffer(), checked((int)stream.Length));
    }
}
