using System.Net;
using System.Text;
using toimi.tools.verkko.Fetcher;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class WebFetcherTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(respond(request));
    }
  }

  private static WebFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> respond)
  {
    return new WebFetcher(new HttpClient(new StubHandler(respond)));
  }

  private const string Html = "<html><body><script>var x = 1;</script><p>Real content</p></body></html>";

  [Theory]
  [InlineData("text/html")]
  [InlineData("TEXT/HTML")]
  [InlineData("application/xhtml+xml")]
  public async Task Html_media_types_are_extracted_regardless_of_case(string mediaType)
  {
    // Media-type case is insensitive per RFC 9110; a server sending TEXT/HTML
    // must not get raw markup (scripts included) dumped into model context.
    var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(Html, Encoding.UTF8, mediaType),
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.Contains("Real content", result.Content);
    Assert.DoesNotContain("var x = 1", result.Content);
  }

  [Fact]
  public async Task Missing_content_type_passes_raw_body_through_as_unknown()
  {
    var fetcher = Fetcher(_ =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("plain body") };
      response.Content.Headers.ContentType = null;
      return response;
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.Equal("unknown", result.ContentType);
    Assert.Equal("plain body", result.Content);
  }

  [Fact]
  public async Task Non_success_status_returns_body_without_throwing()
  {
    var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
    {
      Content = new StringContent("upstream sad"),
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.Equal(502, result.StatusCode);
    Assert.Equal("upstream sad", result.Content);
  }

  [Fact]
  public async Task Overlong_content_is_truncated_with_a_marker()
  {
    var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(new string('a', 60_000)),
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.EndsWith("[Content truncated]", result.Content);
    Assert.True(result.Content.Length < 60_000);
  }
}
