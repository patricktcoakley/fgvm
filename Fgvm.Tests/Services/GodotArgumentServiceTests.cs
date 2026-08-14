using Fgvm.Services;

namespace Fgvm.Tests.Services;

public class GodotArgumentServiceTests
{
    private readonly GodotArgumentService _service = new();

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("   ")]
    public void ShouldForceAttachedMode_WithNullOrEmptyArguments_ReturnsFalse(string? argumentString)
    {
        var result = _service.ShouldForceAttachedMode(argumentString);
        Assert.False(result);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--version")]
    [InlineData("--verbose")]
    [InlineData("-v")]
    [InlineData("--quiet")]
    [InlineData("-q")]
    [InlineData("--quit")]
    [InlineData("--quit-after")]
    [InlineData("--no-header")]
    public void ShouldForceAttachedMode_WithGeneralOptions_ReturnsTrue(string argument)
    {
        var result = _service.ShouldForceAttachedMode(argument);
        Assert.True(result);
    }

    [Theory]
    [InlineData("--debug")]
    [InlineData("-d")]
    [InlineData("--print-fps")]
    [InlineData("--debug-stringnames")]
    [InlineData("--gpu-profile")]
    [InlineData("--profiling")]
    [InlineData("--benchmark")]
    [InlineData("--benchmark-file")]
    public void ShouldForceAttachedMode_WithDebugOptions_ReturnsTrue(string argument)
    {
        var result = _service.ShouldForceAttachedMode(argument);
        Assert.True(result);
    }

    [Theory]
    [InlineData("--script")]
    [InlineData("-s")]
    [InlineData("--check-only")]
    [InlineData("--import")]
    [InlineData("--export-release")]
    [InlineData("--export-debug")]
    [InlineData("--export-pack")]
    [InlineData("--export-patch")]
    [InlineData("--convert-3to4")]
    [InlineData("--validate-conversion-3to4")]
    [InlineData("--doctool")]
    [InlineData("--gdscript-docs")]
    [InlineData("--gdextension-docs")]
    [InlineData("--build-solutions")]
    [InlineData("--dump-gdextension-interface")]
    [InlineData("--dump-extension-api")]
    [InlineData("--dump-extension-api-with-docs")]
    [InlineData("--validate-extension-api")]
    [InlineData("--install-android-build-template")]
    public void ShouldForceAttachedMode_WithStandaloneTools_ReturnsTrue(string argument)
    {
        var result = _service.ShouldForceAttachedMode(argument);
        Assert.True(result);
    }

    [Fact]
    public void ShouldForceAttachedMode_WithHeadlessOption_ReturnsTrue()
    {
        var result = _service.ShouldForceAttachedMode("--headless");
        Assert.True(result);
    }

    [Theory]
    [InlineData("--main-pack")]
    [InlineData("--resolution")]
    [InlineData("--windowed")]
    [InlineData("--maximized")]
    [InlineData("--fullscreen")]
    [InlineData("--position")]
    [InlineData("path/to/project")]
    public void ShouldForceAttachedMode_WithNonTerminalRequiringArguments_ReturnsFalse(string argument)
    {
        var result = _service.ShouldForceAttachedMode(argument);
        Assert.False(result);
    }

    [Theory]
    [InlineData("--help --version")]
    [InlineData("--verbose --debug")]
    [InlineData("--script myscript.gd")]
    [InlineData("--HELP")] // Test case insensitive
    [InlineData("--VERSION")]
    [InlineData("project.godot --script test.gd")]
    public void ShouldForceAttachedMode_WithMultipleArgumentsIncludingTerminalRequired_ReturnsTrue(string arguments)
    {
        var result = _service.ShouldForceAttachedMode(arguments);
        Assert.True(result);
    }

    [Theory]
    [InlineData("project.godot --resolution 1920x1080")]
    [InlineData("--windowed --position 100,100")]
    [InlineData("path/to/project --main-pack game.pck")]
    public void ShouldForceAttachedMode_WithMultipleArgumentsNotRequiringTerminal_ReturnsFalse(string arguments)
    {
        var result = _service.ShouldForceAttachedMode(arguments);
        Assert.False(result);
    }
}
