using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.verkko.Fetcher;
using Toimi.Core.Tools;

namespace toimi.tools.verkko.Tools;

[McpServerToolType]
public class FetchUrlTool(WebFetcher fetcher, FetchCache cache)
{
  [McpServerTool, Description("Fetch a URL and extract its text content. Works with web pages (HTML extracted to readable text), JSON APIs, and plain text. Results are cached for 5 minutes. If the result looks like an empty shell or says JavaScript is required, use selain's browse tool instead.")]
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

    return await ToolGuard.RunAsync(async () =>
    {
      var result = await fetcher.FetchAsync(url, CancellationToken.None);
      cache.Set(url, result);
      return FormatResult(result, fromCache: false);
    }, translate: ex => ex switch
    {
      HttpRequestException http => $"HTTP error fetching {url}: {Reason(http)}",
      TaskCanceledException => $"Request timed out fetching {url}",
      _ => null,
    });
  }

  private static string Reason(HttpRequestException ex)
  {
    return ex.InnerException is { Message.Length: > 0 } inner && !ex.Message.Contains(inner.Message)
      ? $"{ex.Message} ({inner.Message})"
      : ex.Message;
  }

  private static string FormatResult(FetchResult result, bool fromCache)
  {
    var cacheNote = fromCache ? " (from cache)" : "";
    return $"URL: {result.Url}{cacheNote}\nStatus: {result.StatusCode}\nContent-Type: {result.ContentType}\n\n{result.Content}";
  }
}
