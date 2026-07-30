using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests;

public class UrlPolicyTests
{
  private static UrlPolicy Policy(params string[] allowedPrivate)
  {
    return new UrlPolicy(new SelainOptions { AllowedPrivateHosts = [.. allowedPrivate] });
  }

  [Theory]
  [InlineData("https://example.com/page")]
  [InlineData("http://example.com")]
  public void Validate_accepts_public_http_and_https(string url)
  {
    var (ok, error, uri) = Policy().Validate(url);
    Assert.True(ok);
    Assert.Null(error);
    Assert.NotNull(uri);
  }

  [Theory]
  [InlineData("ftp://example.com")]
  [InlineData("javascript:alert(1)")]
  [InlineData("not a url")]
  [InlineData("/relative/path")]
  public void Validate_rejects_non_http_or_malformed(string url)
  {
    var (ok, error, _) = Policy().Validate(url);
    Assert.False(ok);
    Assert.Contains("http", error);
  }

  [Theory]
  [InlineData("https://localhost/admin")]
  [InlineData("http://10.1.2.3/")]
  [InlineData("http://192.168.1.1/")]
  [InlineData("http://toimi-tools-tietue.apps.svc.cluster.local/sse")]
  [InlineData("http://router/")]
  public void Validate_rejects_private_and_internal_hosts(string url)
  {
    var (ok, error, _) = Policy().Validate(url);
    Assert.False(ok);
    Assert.Contains("private or internal", error);
  }

  [Fact]
  public void Validate_allows_explicitly_allowlisted_private_host()
  {
    var (ok, _, _) = Policy("127.0.0.1").Validate("http://127.0.0.1:5000/fixture");
    Assert.True(ok);
  }

  [Fact]
  public void IsAllowedHost_blocks_private_but_respects_allowlist()
  {
    var policy = Policy("127.0.0.1");
    Assert.True(policy.IsAllowedHost("example.com"));
    Assert.True(policy.IsAllowedHost("127.0.0.1"));
    Assert.False(policy.IsAllowedHost("10.255.255.1"));
    Assert.False(policy.IsAllowedHost("localhost"));
  }
}
