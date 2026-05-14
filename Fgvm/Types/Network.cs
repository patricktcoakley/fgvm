namespace Fgvm.Types;

/// <summary>
///     Represents the possible errors that can occur during network operations.
/// </summary>
public abstract record NetworkError
{
    public record RequestFailure(string Url, int StatusCode, string? Body = null) : NetworkError;

    public record ConnectionFailure(string Message, string? Details = null) : NetworkError;

    public record CacheReadFailure(FileOperationError Error) : NetworkError
    {
        public override string ToString() => $"Unable to read release cache: {Error}";
    }

    public record CacheWriteFailure(FileOperationError Error) : NetworkError
    {
        public override string ToString() => $"Unable to write release cache: {Error}";
    }
}
