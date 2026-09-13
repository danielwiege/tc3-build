using Tc3Build.Core;
using Xunit;

namespace Tc3Build.Tests;

public sealed class SdkApiTests
{
    [Fact]
    public void Request_defaults_to_a_visible_build_operation()
    {
        var request = new Tc3BuildRequest("Project.tsproj");

        Assert.Equal(Tc3BuildOperation.Build, request.Operation);
        Assert.False(request.Silent);
        Assert.Null(request.Configuration);
        Assert.Null(request.Platform);
    }

    [Fact]
    public void Runner_is_available_as_a_public_reusable_api()
    {
        Assert.NotNull(new Tc3BuildRunner());
    }
}
