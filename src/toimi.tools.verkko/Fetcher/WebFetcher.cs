namespace toimi.tools.verkko.Fetcher;

public class WebFetcher(HttpClient httpClient)
{
  private const int MaxContentLength = 50_000;

  public async Task<FetchResult> FetchAsync(string url, CancellationToken ct)
  {
    var response = await httpClient.GetAsync(url, ct);
    var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
    var raw = await response.Content.ReadAsStringAsync(ct);

    var content = contentType switch
    {
      "text/html" => HtmlExtractor.ExtractText(raw),
      _ => raw
    };

    if (content.Length > MaxContentLength)
    {
      content = content[..MaxContentLength] + "\n\n[Content truncated]";
    }

    return new FetchResult(url, (int)response.StatusCode, contentType, content);
  }
}
