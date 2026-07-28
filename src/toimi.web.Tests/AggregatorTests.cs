using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Http;
using Toimi.Core.Admin;
using Toimi.Web.Admin;
using Xunit;

namespace Toimi.Web.Tests;

public class AggregatorTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(handler(request));
    }
  }

  private sealed class StubFactory(Dictionary<string, HttpMessageHandler> handlers) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
    {
      return new(handlers[name]) { BaseAddress = new Uri("http://localhost") };
    }
  }

  [Fact]
  public async Task Merges_items_by_UpdatedAt_desc_and_collects_errors()
  {
    var now = DateTimeOffset.UtcNow;
    var muistioItem = new AdminSummaryDto("a", "memory", "older", null, now.AddHours(-2), now.AddHours(-2));
    var ajastinItem = new AdminSummaryDto("b", "schedule", "newer", null, now.AddHours(-1), now.AddHours(-1));

    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-muistio"] = new StubHandler(_ =>
      {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { muistioItem }) };
        return msg;
      }),
      ["admin-ajastin"] = new StubHandler(_ =>
      {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(new[] { ajastinItem }) };
        return msg;
      }),
      ["admin-muistutin"] = new StubHandler(_ => throw new HttpRequestException("boom")),
    };
    var factory = new StubFactory(handlers);

    var result = await AdminAggregator.AggregateAsync(
        ["muistio", "ajastin", "muistutin"], factory, q: null, limit: 50);

    Assert.Equal(2, result.Items.Count);
    Assert.Equal("b", result.Items[0].Id); // newer first
    Assert.Equal("a", result.Items[1].Id);
    var err = Assert.Single(result.Errors);
    Assert.Equal("muistutin", err.Tool);
  }
}

public class ForwarderTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(handler(request));
    }
  }

  private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
    {
      return new(handler) { BaseAddress = new Uri("http://upstream") };
    }
  }

  [Fact]
  public async Task Unknown_tool_returns_404()
  {
    var ctx = new DefaultHttpContext();
    ctx.Request.Method = "GET";
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
    var result = await AdminForwarder.ForwardAsync("notreal", "items", ctx, opts, factory);
    Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.NotFound>(result);
  }

  [Fact]
  public async Task Forwards_query_and_method()
  {
    HttpRequestMessage? captured = null;
    var handler = new StubHandler(req =>
    {
      captured = req;
      return new HttpResponseMessage(HttpStatusCode.OK)
      { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
    });
    var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    ctx.Request.Method = "GET";
    ctx.Request.QueryString = new QueryString("?q=foo&page=2");
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.NotNull(captured);
    Assert.Equal(HttpMethod.Get, captured!.Method);
    Assert.Equal("/admin/items?q=foo&page=2", captured.RequestUri!.PathAndQuery);
    Assert.Equal(200, ctx.Response.StatusCode);
  }

  [Fact]
  public async Task Does_not_forward_hop_by_hop_transfer_encoding_header()
  {
    var handler = new StubHandler(_ =>
    {
      var msg = new HttpResponseMessage(HttpStatusCode.OK)
      { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") };
      // HttpClient de-chunks the body but keeps this header; a proxy must not forward it.
      msg.Headers.TransferEncodingChunked = true;
      return msg;
    });
    var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    ctx.Request.Method = "GET";
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.False(
        ctx.Response.Headers.ContainsKey("Transfer-Encoding"),
        "Forwarding upstream Transfer-Encoding over an already-de-chunked body breaks response framing and hangs the client.");
  }
}
