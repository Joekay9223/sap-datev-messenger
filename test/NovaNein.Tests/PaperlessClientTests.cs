using NovaNein.Server;

namespace NovaNein.Tests;

public sealed class PaperlessClientTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.20.30.40")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.0.2.11")]
    public void PrivateHttpHost_AcceptsRfc1918AndLoopback(string host)
    {
        Assert.True(PaperlessClient.IsPrivateHttpHost(host));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("paperless.example.com")]
    public void PrivateHttpHost_RejectsPublicOrUnresolvedHosts(string host)
    {
        Assert.False(PaperlessClient.IsPrivateHttpHost(host));
    }
}
