namespace Fgvm.Extensions;

/// <summary>
///     Explicit decimal and binary byte-unit multipliers.
/// </summary>
internal static class ByteSize
{
    public const long Kilobyte = 1_000L;
    public const long Megabyte = 1_000_000L;
    public const long Kibibyte = 1L << 10;
    public const long Mebibyte = 1L << 20;
}
