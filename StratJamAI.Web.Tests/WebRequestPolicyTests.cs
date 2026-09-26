using Microsoft.AspNetCore.Http;
using StratJamAI.Web;
using Xunit;

namespace StratJamAI.Web.Tests;

public sealed class WebRequestPolicyTests
{
    [Theory]
    [InlineData("https", "play.example.com", "https://play.example.com", true)]
    [InlineData("https", "play.example.com", "https://PLAY.EXAMPLE.COM:443", true)]
    [InlineData("http", "192.168.1.2:5080", "http://192.168.1.2:5080", true)]
    [InlineData("http", "127.0.0.1:5080", "http://localhost:5080", false)]
    [InlineData("https", "play.example.com", "https://attacker.example", false)]
    [InlineData("https", "play.example.com", "https://play.example.com:444", false)]
    [InlineData("https", "play.example.com", "http://play.example.com", false)]
    [InlineData("https", "play.example.com", "https://user@play.example.com", false)]
    [InlineData("https", "play.example.com", "null", false)]
    public void AllowsOnlyTheSameBrowserOrigin(string scheme, string host, string origin, bool expected)
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "POST";
        request.Scheme = scheme;
        request.Host = new HostString(host);
        request.Headers.Origin = origin;
        Assert.Equal(expected, WebRequestPolicy.AllowsMutation(request));
    }

    [Fact]
    public void RejectsCrossSiteMetadataAndAllowsNonBrowserClients()
    {
        var request = new DefaultHttpContext().Request;
        request.Method = "POST";
        Assert.True(WebRequestPolicy.AllowsMutation(request));
        request.Headers["Sec-Fetch-Site"] = "cross-site";
        Assert.False(WebRequestPolicy.AllowsMutation(request));
    }
}
