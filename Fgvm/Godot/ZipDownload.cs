namespace Fgvm.Godot;

public sealed class ZipDownload(Stream stream, long? contentLength = null, IDisposable? owner = null) : IDisposable, IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public long? ContentLength { get; } = contentLength;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
        owner?.Dispose();
    }

    public void Dispose()
    {
        Stream.Dispose();
        owner?.Dispose();
    }
}
