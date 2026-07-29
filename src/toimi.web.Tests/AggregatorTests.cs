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

  [Fact]
  public async Task Null_query_produces_an_empty_q_parameter()
  {
    Uri? captured = null;
    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-muistio"] = new StubHandler(req =>
      {
        captured = req.RequestUri;
        return new HttpResponseMessage(HttpStatusCode.OK)
        { Content = JsonContent.Create(Array.Empty<AdminSummaryDto>()) };
      }),
    };
    var factory = new StubFactory(handlers);

    await AdminAggregator.AggregateAsync(["muistio"], factory, q: null, limit: 50);

    Assert.NotNull(captured);
    Assert.Contains("q=&", captured.PathAndQuery);
  }

  [Fact]
  public async Task All_tools_failing_yields_empty_items_and_all_errors()
  {
    var handlers = new Dictionary<string, HttpMessageHandler>
    {
      ["admin-muistio"] = new StubHandler(_ => throw new HttpRequestException("boom1")),
      ["admin-ajastin"] = new StubHandler(_ => throw new HttpRequestException("boom2")),
    };
    var factory = new StubFactory(handlers);

    var result = await AdminAggregator.AggregateAsync(["muistio", "ajastin"], factory, q: null, limit: 50);

    Assert.Empty(result.Items);
    Assert.Equal(2, result.Errors.Count);
    Assert.Equal(["muistio", "ajastin"], result.Errors.Select(e => e.Tool));
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

  [Fact]
  public async Task Put_forwards_body_and_content_type()
  {
    HttpRequestMessage? captured = null;
    var handler = new StubHandler(req =>
    {
      captured = req;
      return new HttpResponseMessage(HttpStatusCode.OK)
      { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
    });
    var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    ctx.Request.Method = "PUT";
    ctx.Request.ContentType = "application/json";
    var bodyBytes = System.Text.Encoding.UTF8.GetBytes("test-body-payload");
    ctx.Request.Body = new MemoryStream(bodyBytes);
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.NotNull(captured);
    Assert.Equal(HttpMethod.Put, captured.Method);
    Assert.NotNull(captured.Content);
    var forwardedBytes = await captured.Content.ReadAsByteArrayAsync();
    Assert.Equal(bodyBytes, forwardedBytes);
    Assert.Equal("application/json", captured.Content.Headers.ContentType?.MediaType);
  }

  [Fact]
  public async Task Get_does_not_forward_a_body()
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
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.NotNull(captured);
    Assert.Null(captured.Content);
  }

  [Fact]
  public async Task If_unmodified_since_passes_through()
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
    ctx.Request.Headers.IfUnmodifiedSince = "Wed, 21 Oct 2015 07:28:00 GMT";
    ctx.Request.Headers["X-Custom"] = "should-not-forward";
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.NotNull(captured);
    Assert.True(captured.Headers.TryGetValues("If-Unmodified-Since", out var values));
    Assert.Equal("Wed, 21 Oct 2015 07:28:00 GMT", Assert.Single(values));
    Assert.False(captured.Headers.Contains("X-Custom"));
  }

  [Fact]
  public async Task Upstream_error_status_and_body_propagate()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict)
    { Content = new StringContent("conflict body", System.Text.Encoding.UTF8, "application/json") });
    var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    ctx.Request.Method = "GET";
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    Assert.Equal(409, ctx.Response.StatusCode);
    ctx.Response.Body.Position = 0;
    using var reader = new StreamReader(ctx.Response.Body);
    Assert.Equal("conflict body", await reader.ReadToEndAsync());
  }

  [Fact]
  public async Task Unreachable_upstream_maps_to_502()
  {
    var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));
    var ctx = new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    ctx.Request.Method = "GET";
    var opts = new AdminToolsOptions { Tools = ["muistio"] };
    var factory = new StubFactory(handler);

    var result = await AdminForwarder.ForwardAsync("muistio", "items", ctx, opts, factory);

    var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(result);
    Assert.Equal(502, problem.StatusCode);
  }
}
