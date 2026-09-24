using BraviaTheatre.Core.Engine;

namespace BraviaTheatre.Tests;

public class ControlEndpointTests
{
    [Theory]
    [InlineData("192.168.1.50", "192.168.1.50")]
    [InlineData("  192.168.1.50  ", "192.168.1.50")]
    [InlineData("Bravia-Bar.local", "bravia-bar.local")]
    [InlineData("soundbar", "soundbar")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("[fd00::20]", "fd00::20")]
    public void AcceptsBareHostNamesAndAddresses(string input, string expected)
    {
        Assert.True(ControlEndpoint.TryNormalizeHost(input, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://192.168.1.50")]
    [InlineData("192.168.1.50:55051")]
    [InlineData("192.168.1.50/control")]
    [InlineData("living room")]
    [InlineData("12345")]
    [InlineData("192.168.1")]
    [InlineData("192.168.1.256")]
    [InlineData("fe80::1::2")]
    public void RejectsSchemesPortsPathsAndShorthandAddresses(string input)
    {
        Assert.False(ControlEndpoint.TryNormalizeHost(input, out _));
    }

    [Fact]
    public void CreateAddressBracketsIPv6Literals()
    {
        Assert.Equal("http://192.168.1.50:55051/", ControlEndpoint.CreateAddress("192.168.1.50", 55051).AbsoluteUri);
        Assert.Equal("http://[fd00::20]:55051/", ControlEndpoint.CreateAddress("fd00::20", 55051).AbsoluteUri);
        Assert.Equal("http://[fd00::20]:55051/", ControlEndpoint.CreateAddress("[fd00::20]", 55051).AbsoluteUri);
        Assert.Equal(55051, ControlEndpoint.CreateAddress("fe80::1%12", 55051).Port);
    }

    [Fact]
    public void CreateAddressRejectsInvalidHostsAndPorts()
    {
        Assert.Throws<ArgumentException>(() => ControlEndpoint.CreateAddress("http://192.168.1.50", 55051));
        Assert.Throws<ArgumentOutOfRangeException>(() => ControlEndpoint.CreateAddress("192.168.1.50", 0));
    }
}
