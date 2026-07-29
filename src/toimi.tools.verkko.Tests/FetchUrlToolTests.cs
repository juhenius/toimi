using System.Net;
using System.Text;
using toimi.tools.verkko.Fetcher;
using toimi.tools.verkko.Tools;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class FetchUrlToolTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
      RequestCount++;
      return Task.FromResult(respond(request));
    }
  }

  private static int CountOccurrences(string haystack, string needle)
  {
    var count = 0;
    var index = 0;
    while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
      count++;
      index += needle.Length;
    }

    return count;
  }

  [Fact]
  public async Task Second_fetch_serves_from_cache_with_a_note()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("hello", Encoding.UTF8, "text/plain"),
    });
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient(handler)), new FetchCache());

    await tool.FetchUrl("http://example.test/a");
    var second = await tool.FetchUrl("http://example.test/a");

    Assert.Equal(1, handler.RequestCount);
    Assert.Contains("(from cache)", second);
  }

  [Fact]
  public async Task Skip_cache_bypasses_read_but_refreshes_entry()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("hello", Encoding.UTF8, "text/plain"),
    });
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient(handler)), new FetchCache());

    await tool.FetchUrl("http://example.test/a");
    await tool.FetchUrl("http://example.test/a", skipCache: true);
    await tool.FetchUrl("http://example.test/a");

    // The skipCache call re-fetched (2 upstream requests), and refreshed the
    // cache entry so the third (normal) call is still served from cache.
    Assert.Equal(2, handler.RequestCount);
  }

  [Fact]
  public async Task Http_error_composes_inner_reason_once()
  {
    var handler = new StubHandler(_ => throw new HttpRequestException("outer", new InvalidOperationException("inner detail")));
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient(handler)), new FetchCache());

    var result = await tool.FetchUrl("http://example.test/a");

    Assert.Contains("outer", result);
    Assert.Contains("inner detail", result);
    Assert.Equal(1, CountOccurrences(result, "inner detail"));
  }

  [Fact]
  public async Task Http_error_skips_appending_when_outer_already_contains_inner()
  {
    // Discriminates the `!ex.Message.Contains(inner.Message)` guard: the outer
    // message already carries the inner detail, so it must not be appended a
    // second time. Inverting the guard would make this fail with 2 occurrences.
    var handler = new StubHandler(_ => throw new HttpRequestException(
      "connection failed: inner detail", new InvalidOperationException("inner detail")));
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient(handler)), new FetchCache());

    var result = await tool.FetchUrl("http://example.test/a");

    Assert.Equal(1, CountOccurrences(result, "inner detail"));
  }

  [Fact]
  public async Task Timeout_maps_to_the_timed_out_message()
  {
    var handler = new StubHandler(_ => throw new TaskCanceledException());
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient(handler)), new FetchCache());

    var result = await tool.FetchUrl("http://example.test/a");

    Assert.Equal("Request timed out fetching http://example.test/a", result);
  }

  [Fact]
  public async Task Non_success_responses_are_cached()
  {
    // PIN: FetchUrl calls cache.Set unconditionally after any non-throwing
    // fetch, so a 5xx upstream response is cached for the full 5-minute TTL
    // just like a success. This looks deliberate but is surfaced here for a
    // future decision, not changed as part of this characterization pass.
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
    {
      Content = new StringContent("upstream sad"),
    });
    var tool = new FetchUrlTool(new WebFetcher(new HttpClient(handler)), new FetchCache());

    await tool.FetchUrl("http://example.test/a");
    await tool.FetchUrl("http://example.test/a");

    Assert.Equal(1, handler.RequestCount);
  }
}
