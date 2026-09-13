using Tc3Build.Infrastructure;
using Xunit;

namespace Tc3Build.Tests;

public sealed class AutomationHostCatalogTests
{
    [Fact]
    public void Automatic_order_prefers_newest_visual_studio_before_xae_shell()
    {
        var hosts = AutomationHostCatalog.Resolve("auto");

        Assert.Equal(
            ["vs2026", "vs2022", "vs2019", "xae2022", "xae2019", "xae2017", "xae"],
            hosts.Select(host => host.Key));
        Assert.True(hosts.Take(3).All(host => host.IsVisualStudio));
        Assert.True(hosts.Skip(3).All(host => !host.IsVisualStudio));
    }

    [Theory]
    [InlineData("vs2026", "VisualStudio.DTE.18.0", true)]
    [InlineData("vs2022", "VisualStudio.DTE.17.0", true)]
    [InlineData("vs2019", "VisualStudio.DTE.16.0", true)]
    [InlineData("xae2022", "TcXaeShell.DTE.17.0", false)]
    [InlineData("xae2019", "TcXaeShell.DTE.16.0", false)]
    [InlineData("xae2017", "TcXaeShell.DTE.15.0", false)]
    [InlineData("xae", "TcXaeShell.DTE", false)]
    public void Every_supported_host_can_be_selected_by_key(
        string key,
        string progId,
        bool isVisualStudio)
    {
        var host = Assert.Single(AutomationHostCatalog.Resolve(key));

        Assert.Equal(key, host.Key);
        Assert.Equal(progId, host.ProgId);
        Assert.Equal(isVisualStudio, host.IsVisualStudio);
    }

    [Theory]
    [InlineData("VisualStudio.DTE.18.0", "vs2026")]
    [InlineData("VisualStudio.DTE.17.0", "vs2022")]
    [InlineData("VisualStudio.DTE.16.0", "vs2019")]
    [InlineData("TcXaeShell.DTE.17.0", "xae2022")]
    [InlineData("TcXaeShell.DTE.16.0", "xae2019")]
    [InlineData("TcXaeShell.DTE.15.0", "xae2017")]
    public void Every_supported_host_can_also_be_selected_by_com_progid(
        string progId,
        string expectedKey)
    {
        var host = Assert.Single(AutomationHostCatalog.Resolve(progId));

        Assert.Equal(expectedKey, host.Key);
    }

    [Fact]
    public void Unknown_host_is_rejected_with_supported_values()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => AutomationHostCatalog.Resolve("vs2018"));

        Assert.Contains("vs2019", exception.Message);
        Assert.Contains("xae2019", exception.Message);
    }
}
