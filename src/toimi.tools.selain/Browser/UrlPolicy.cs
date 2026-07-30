namespace toimi.tools.selain.Browser;

/// <summary>
/// Navigation policy: http(s) only, no private/internal hosts (SSRF guard shared
/// with verkko via Toimi.Core.Net.PrivateAddress). AllowedPrivateHosts exists for
/// integration tests that serve fixture pages on loopback.
/// </summary>
public class UrlPolicy(SelainOptions options)
{
  public (bool Ok, string? Error, Uri? Uri) Validate(string url)
  {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
      || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
      return (false, "Invalid URL. Must be an absolute URL starting with http:// or https://", null);
    }

    return IsAllowedHost(uri.DnsSafeHost)
      ? (true, null, uri)
      : (false, $"Blocked URL: '{uri.DnsSafeHost}' is a private or internal host.", null);
  }

  public bool IsAllowedHost(string host)
  {
    return options.AllowedPrivateHosts.Contains(host, StringComparer.OrdinalIgnoreCase)
      || !Toimi.Core.Net.PrivateAddress.IsBlockedHost(host);
  }
}
