using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.verkko.Fetcher;

namespace toimi.tools.verkko.Tools;

[McpServerToolType]
public class FetchUrlTool(WebFetcher fetcher, FetchCache cache)
{
  [McpServerTool, Description("Fetch a URL and extract its text content. Works with web pages (HTML extracted to readable text), JSON APIs, and plain text. Results are cached for 5 minutes.")]
  public async Task<string> FetchUrl(
    [Description("The URL to fetch (must start with http:// or https://)")] string url,
    [Description("Skip cache and fetch fresh content (default false)")] bool skipCache = false)
  {
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
      || (uri.Scheme != "http" && uri.Scheme != "https"))
    {
      return "Invalid URL. Must be an absolute URL starting with http:// or https://";
    }

    if (UrlGuard.IsBlockedHost(uri.DnsSafeHost))
    {
      return $"Blocked URL: '{uri.DnsSafeHost}' is a private or internal host.";
    }

    if (!skipCache)
    {
      var cached = cache.Get(url);
      if (cached is not null)
      {
        return FormatResult(cached, fromCache: true);
      }
    }

    FetchResult result;
    try
    {
      result = await fetcher.FetchAsync(url, CancellationToken.None);
    }
    catch (HttpRequestException ex)
    {
      var reason = ex.InnerException is { Message.Length: > 0 } inner && !ex.Message.Contains(inner.Message)
        ? $"{ex.Message} ({inner.Message})"
        : ex.Message;
      return $"HTTP error fetching {url}: {reason}";
    }
    catch (TaskCanceledException)
    {
      return $"Request timed out fetching {url}";
    }

    cache.Set(url, result);
    return FormatResult(result, fromCache: false);
  }

  private static string FormatResult(FetchResult result, bool fromCache)
  {
    var cacheNote = fromCache ? " (from cache)" : "";
    return $"URL: {result.Url}{cacheNote}\nStatus: {result.StatusCode}\nContent-Type: {result.ContentType}\n\n{result.Content}";
  }
}
