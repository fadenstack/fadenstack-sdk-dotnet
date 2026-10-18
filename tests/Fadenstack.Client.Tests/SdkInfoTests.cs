using System.Reflection;
using Xunit;

namespace Fadenstack.Client.Tests;

public class SdkInfoTests
{
    [Theory]
    [InlineData("0.2.1", "0.2.1")]
    [InlineData("0.2.0-rc.3", "0.2.0-rc.3")]
    [InlineData("0.2.0-rc.3+4ac729f1d2e", "0.2.0-rc.3")]
    [InlineData("0.2.1+4ac729f1d2e", "0.2.1")]
    public void The_version_is_the_package_version_with_its_prerelease_and_without_the_commit(string informational, string expected)
    {
        Assert.Equal(expected, SdkInfo.Parse(informational, new Version(9, 9, 9, 0)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("+4ac729f")]
    public void Without_an_informational_version_it_is_the_assembly_version(string? informational)
    {
        Assert.Equal("0.2.1", SdkInfo.Parse(informational, new Version(0, 2, 1, 0)));
        Assert.Equal("1.4.0", SdkInfo.Parse(informational, new Version(1, 4)));
        Assert.Equal("", SdkInfo.Parse(informational, null));
    }

    [Fact]
    public void The_sdk_says_the_version_it_was_built_as()
    {
        var assembly = typeof(SdkInfo).Assembly;
        var built = assembly.GetName().Version!;
        Assert.StartsWith($"{built.Major}.{built.Minor}.{built.Build}", SdkInfo.Version);
        Assert.DoesNotContain("+", SdkInfo.Version);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.StartsWith(SdkInfo.Version, informational);
    }
}
