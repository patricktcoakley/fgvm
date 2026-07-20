using System.IO.Compression;

namespace Fgvm.Tests.TestSupport;

internal static class ZipArchiveTestBuilder
{
    internal static byte[] CreateArchive(params (string Name, string Contents)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                AddEntry(archive, name, contents);
            }
        }

        return stream.ToArray();
    }

    internal static byte[] CreateArchive((string Name, string Contents)[] entries, string payloadEntryName, int payloadBytes)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, contents) in entries)
            {
                AddEntry(archive, name, contents);
            }

            if (payloadBytes > 0)
            {
                AddPayloadEntry(archive, payloadEntryName, payloadBytes);
            }
        }

        return stream.ToArray();
    }

    internal static ZipArchiveEntry AddEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
        return entry;
    }

    internal static void AddPayloadEntry(ZipArchive archive, string name, int payloadBytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        using var stream = entry.Open();
        var buffer = new byte[8192];
        for (var i = 0; i < buffer.Length; i++)
        {
            buffer[i] = (byte)(i % 251);
        }

        var remaining = payloadBytes;
        while (remaining > 0)
        {
            var write = Math.Min(buffer.Length, remaining);
            stream.Write(buffer, 0, write);
            remaining -= write;
        }
    }
}
