using Fgvm.Extensions;

namespace Fgvm.Tests.Extensions;

public sealed class ByteSizeTests
{
    [Fact]
    public void DecimalAndBinaryUnits_AreDistinct()
    {
        Assert.Equal(1_000L, ByteSize.Kilobyte);
        Assert.Equal(1_000_000L, ByteSize.Megabyte);
        Assert.Equal(1_024L, ByteSize.Kibibyte);
        Assert.Equal(1_048_576L, ByteSize.Mebibyte);
    }
}
