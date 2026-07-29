namespace toimi.tools.verkko.Fetcher;

public class WebFetcher(HttpClient httpClient)
{
  private const int MaxContentLength = 50_000;

  public async Task<FetchResult> FetchAsync(string url, CancellationToken ct)
  {
    var response = await httpClient.GetAsync(url, ct);
    var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
    var raw = await response.Content.ReadAsStringAsync(ct);

    // Media-type case is insensitive (RFC 9110), and XHTML deserves the same
    // extraction; match loosely rather than on the exact lowercase literal.
    var content = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
      ? HtmlExtractor.ExtractText(raw)
      : raw;

    if (content.Length > MaxContentLength)
    {
      content = content[..MaxContentLength] + "\n\n[Content truncated]";
    }

    return new FetchResult(url, (int)response.StatusCode, contentType, content);
  }
}
