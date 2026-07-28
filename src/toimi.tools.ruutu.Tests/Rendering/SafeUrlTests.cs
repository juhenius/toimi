using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class SafeUrlTests
{
  [Fact]
  public void Passes_a_normal_https_url_unchanged()
  {
    Assert.Equal("https://posti.fi/track/123", ScribanRenderer.SafeUrl("https://posti.fi/track/123"));
  }

  [Fact]
  public void Escapes_ampersands_in_query_for_attribute_context()
  {
    Assert.Equal("https://t.test/p?a=1&amp;b=2", ScribanRenderer.SafeUrl("https://t.test/p?a=1&b=2"));
  }

  [Theory]
  [InlineData("http://example.com")]
  [InlineData("javascript:alert(1)")]
  [InlineData("data:text/html,<h1>x</h1>")]
  [InlineData("file:///etc/passwd")]
  [InlineData("ftp://example.com/x")]
  public void Rejects_non_https_schemes(string url)
  {
    Assert.Equal("about:blank", ScribanRenderer.SafeUrl(url));
  }

  [Theory]
  [InlineData("https://localhost/admin")]
  [InlineData("https://router/")]
  [InlineData("https://127.0.0.1/")]
  [InlineData("https://10.0.0.5/")]
  [InlineData("https://192.168.1.1/")]
  [InlineData("https://172.16.4.4/")]
  [InlineData("https://169.254.1.1/")]
  [InlineData("https://[::1]/")]
  [InlineData("https://[fc00::1]/")]
  [InlineData("https://[fe80::1]/")]
  [InlineData("https://[::ffff:10.0.0.5]/")]
  [InlineData("https://[::0a00:0001]/")] // deprecated IPv4-compatible ::a.b.c.d form → 10.0.0.1
  [InlineData("https://0.0.0.0/")]
  [InlineData("https://100.64.0.1/")]
  public void Rejects_loopback_private_and_single_label_hosts(string url)
  {
    Assert.Equal("about:blank", ScribanRenderer.SafeUrl(url));
  }

  [Theory]
  [InlineData(null)]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("not a url")]
  [InlineData("/relative/path")]
  public void Rejects_null_empty_and_non_absolute(string? url)
  {
    Assert.Equal("about:blank", ScribanRenderer.SafeUrl(url));
  }

  [Fact]
  public void Prevents_attribute_breakout()
  {
    var result = ScribanRenderer.SafeUrl("https://x.test/a\"><script>alert(1)</script>");
    Assert.DoesNotContain("\"", result);
    Assert.DoesNotContain("<script>", result);
  }
}
