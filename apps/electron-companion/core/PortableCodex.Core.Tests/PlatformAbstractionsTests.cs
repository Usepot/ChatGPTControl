using PortableCodex.Core.Services;

namespace PortableCodex.Core.Tests;

public sealed class PlatformAbstractionsTests
{
    [Fact]
    public void DefaultAppPathsUsesAnApplicationDataSubdirectory()
    {
        var paths = new DefaultAppPaths("PortableCodex.Tests");

        Assert.EndsWith("PortableCodex.Tests", paths.DataDirectory);
        Assert.Equal(Path.Combine(paths.DataDirectory, "portable-codex-state.json"), paths.StateFilePath);
    }

    [Fact]
    public void PlatformCapabilitiesKeepDesktopAutomationBehindWindowsAdapter()
    {
        var capabilities = new DefaultPlatformCapabilities();

        Assert.Equal(OperatingSystem.IsWindows(), capabilities.SupportsDesktopAutomation);
        Assert.Equal(OperatingSystem.IsWindows(), capabilities.SupportsDesktopCapture);
        Assert.Equal(OperatingSystem.IsWindows(), capabilities.SupportsEmbeddedChat);
    }
}
