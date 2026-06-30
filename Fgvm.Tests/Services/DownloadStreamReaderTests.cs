using System.Diagnostics;
using Fgvm.Services;

namespace Fgvm.Tests.Services;

public sealed class DownloadStreamReaderTests
{
    [Fact]
    public async Task ReadAsync_ThrowsIOException_WhenNoBytesArriveBeforeTimeout()
    {
        var stream = new NeverCompletingStream();
        var buffer = new byte[1024];
        var stopwatch = Stopwatch.StartNew();

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            DownloadStreamReader.ReadAsync(stream, buffer, TimeSpan.FromMilliseconds(25), CancellationToken.None));

        Assert.Contains("Download stalled", exception.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ReadAsync_PreservesCallerCancellation()
    {
        var stream = new NeverCompletingStream();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DownloadStreamReader.ReadAsync(stream, new byte[1024], TimeSpan.FromSeconds(30), cancellation.Token));
    }

    [Fact]
    public async Task ReadAsync_AllowsBriefPauseAfterPartialProgress()
    {
        var stream = new StagedStream(
            new ReadStage([1, 2, 3], TimeSpan.Zero),
            new ReadStage([4, 5], TimeSpan.FromMilliseconds(25)),
            new ReadStage([], TimeSpan.Zero));
        var buffer = new byte[4];

        var firstRead = await DownloadStreamReader.ReadAsync(stream, buffer, TimeSpan.FromMilliseconds(250), CancellationToken.None);
        Assert.Equal(3, firstRead);
        Assert.Equal([1, 2, 3], buffer[..firstRead]);

        var secondRead = await DownloadStreamReader.ReadAsync(stream, buffer, TimeSpan.FromMilliseconds(250), CancellationToken.None);
        Assert.Equal(2, secondRead);
        Assert.Equal([4, 5], buffer[..secondRead]);

        var endOfStream = await DownloadStreamReader.ReadAsync(stream, buffer, TimeSpan.FromMilliseconds(250), CancellationToken.None);
        Assert.Equal(0, endOfStream);
    }

    private readonly record struct ReadStage(byte[] Bytes, TimeSpan Delay);

    private sealed class StagedStream(params ReadStage[] stages) : Stream
    {
        private int _stageIndex;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_stageIndex >= stages.Length)
            {
                return 0;
            }

            var stage = stages[_stageIndex++];
            if (stage.Delay > TimeSpan.Zero)
            {
                await Task.Delay(stage.Delay, cancellationToken);
            }

            stage.Bytes.CopyTo(buffer);
            return stage.Bytes.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
