using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;

namespace Fgvm.Godot.Download;

/// <summary>
///     Validates the probe response and divides the representation into non-overlapping ranges.
/// </summary>
internal sealed record RangeDownloadPlan(
    long TotalBytes,
    IReadOnlyList<RangeChunk> Chunks,
    RangeEntityValidator Validator
)
{
    public static bool TryCreate(HttpResponseMessage probeResponse,
        long chunkSize,
        [NotNullWhen(true)] out RangeDownloadPlan? plan
    )
    {
        var range = probeResponse.Content.Headers.ContentRange;
        var validator = RangeEntityValidator.From(probeResponse);

        // Separate requests are safe to combine only when they identify the same representation
        // and the probe describes a valid first range of the complete file.
        if (validator is null ||
            range is not { From: 0, To: { } probeEnd, Length: { } totalBytes } ||
            !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase) ||
            probeEnd < 0 || probeEnd >= totalBytes || probeEnd >= chunkSize)
        {
            plan = null;
            return false;
        }

        // Reuse the probe body as chunk zero instead of requesting those bytes again.
        var chunks = new List<RangeChunk> { new(0, probeEnd, probeResponse) };
        for (var start = probeEnd + 1; start < totalBytes; start += chunkSize)
        {
            chunks.Add(new RangeChunk(start, Math.Min(start + chunkSize - 1, totalBytes - 1), null));
        }

        plan = new RangeDownloadPlan(totalBytes, chunks, validator);
        return true;
    }
}

/// <summary>
///     An inclusive byte range. Only the probe chunk carries an existing response.
/// </summary>
internal readonly record struct RangeChunk(long Start, long End, HttpResponseMessage? Response)
{
    public long Length => End - Start + 1;
}

/// <summary>
///     A stable representation identifier sent with If-Range to prevent mixing file versions.
/// </summary>
internal sealed record RangeEntityValidator(string? EntityTag, DateTimeOffset? LastModified)
{
    public static RangeEntityValidator? From(HttpResponseMessage response)
    {
        // Strong ETags are the best identity check. Last-Modified is the HTTP fallback when
        // the server does not provide one; weak ETags are not valid for If-Range.
        if (response.Headers.ETag is { IsWeak: false } etag)
        {
            return new RangeEntityValidator(etag.ToString(), null);
        }

        return response.Content.Headers.LastModified is { } modified
            ? new RangeEntityValidator(null, modified)
            : null;
    }

    public void Apply(HttpRequestMessage request)
    {
        request.Headers.IfRange = (EntityTag, LastModified) switch
        {
            ({ } entityTag, null) => new RangeConditionHeaderValue(new EntityTagHeaderValue(entityTag)),
            (null, { } lastModified) => new RangeConditionHeaderValue(lastModified),
            _ => throw new InvalidOperationException("An If-Range validator requires exactly one value.")
        };
    }
}
