using System.Net;
using Toimi.Core.Net;
using Xunit;

namespace toimi.core.Tests;

public class PrivateAddressTests
{
  [Theory]
  [InlineData("10.0.0.1")]
  [InlineData("127.0.0.1")]
  [InlineData("0.0.0.0")]
  [InlineData("100.64.0.1")]
  [InlineData("169.254.1.1")]
  [InlineData("172.16.0.1")]
  [InlineData("172.31.255.255")]
  [InlineData("192.168.1.1")]
  [InlineData("::1")]
  [InlineData("fc00::1")]
  [InlineData("fe80::1")]
  [InlineData("::ffff:10.0.0.1")]
  [InlineData("100.127.255.255")]
  [InlineData("fd00::1")]
  [InlineData("::10.0.0.1")]
  public void IsPrivate_true_for_internal_addresses(string ip)
  {
    Assert.True(PrivateAddress.IsPrivate(IPAddress.Parse(ip)));
  }

  [Theory]
  [InlineData("93.184.216.34")]
  [InlineData("172.32.0.1")]
  [InlineData("100.128.0.1")]
  [InlineData("2606:4700::1111")]
  [InlineData("172.15.255.255")]
  [InlineData("100.63.255.255")]
  [InlineData("fb00::1")]
  [InlineData("fe00::1")]
  public void IsPrivate_false_for_public_addresses(string ip)
  {
    Assert.False(PrivateAddress.IsPrivate(IPAddress.Parse(ip)));
  }

  [Theory]
  [InlineData("localhost")]
  [InlineData("router")]
  [InlineData("qdrant")]
  [InlineData("192.168.1.1")]
  [InlineData("::1")]
  [InlineData("")]
  public void IsBlockedHost_true_for_internal_hosts(string host)
  {
    Assert.True(PrivateAddress.IsBlockedHost(host));
  }

  [Theory]
  [InlineData("example.com")]
  [InlineData("api.github.com")]
  public void IsBlockedHost_false_for_public_hostnames(string host)
  {
    Assert.False(PrivateAddress.IsBlockedHost(host));
  }

  [Theory]
  [InlineData("toimi-tools-tietue.apps.svc.cluster.local")]
  [InlineData("foo.cluster.local")]
  [InlineData("sub.example.localhost")]
  public void IsBlockedHost_true_for_cluster_internal_suffixes(string host)
  {
    Assert.True(PrivateAddress.IsBlockedHost(host));
  }

  [Theory]
  [InlineData("toimi-tools-tietue.apps.svc.cluster.local.")]
  [InlineData("localhost.")]
  [InlineData("127.0.0.1.")]
  [InlineData("FOO.CLUSTER.LOCAL")]
  public void IsBlockedHost_true_for_trailing_dot_and_case_variants(string host)
  {
    Assert.True(PrivateAddress.IsBlockedHost(host));
  }
}
