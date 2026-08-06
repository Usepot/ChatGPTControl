using PortableCodex.Native.Services;

namespace PortableCodex.Core.Tests;

public sealed class TailscaleStatusParserTests
{
    [Fact]
    public void ParsesRunningStatusWithUppercaseCliFields()
    {
        var result = TailscaleStatusParser.ParseStatus("""
            {"BackendState":"Running","Self":{"DNSName":"companion.tailnet.ts.net."}}
            """);

        Assert.True(result.Ready);
        Assert.Equal("https://companion.tailnet.ts.net", TailscaleStatusParser.FindFunnelUrl(result.DeviceDnsName));
        Assert.Equal("companion.tailnet.ts.net", result.DeviceDnsName);
    }

    [Fact]
    public void ParsesCamelCaseNeedsLoginStatus()
    {
        var result = TailscaleStatusParser.ParseStatus("{\"backendState\":\"NeedsLogin\"}");

        Assert.False(result.Ready);
        Assert.Equal("login_required", result.State);
    }

    [Fact]
    public void ParsesApprovalPendingHumanOutputAsNotReady()
    {
        var result = TailscaleStatusParser.ParseStatus("Funnel is not enabled on your tailnet; visit https://login.tailscale.com/f/funnel");

        Assert.False(result.Ready);
        Assert.Equal("approval_required", result.State);
    }

    [Fact]
    public void FindsFunnelUrlInTextAndUsesFallbackHost()
    {
        Assert.Equal(
            "https://companion.tailnet.ts.net",
            TailscaleStatusParser.FindFunnelUrl("https://companion.tailnet.ts.net/"));
        Assert.Equal(
            "https://companion.tailnet.ts.net",
            TailscaleStatusParser.FindFunnelUrl("funnel status: enabled for companion.tailnet.ts.net", "companion.tailnet.ts.net"));
    }
}
