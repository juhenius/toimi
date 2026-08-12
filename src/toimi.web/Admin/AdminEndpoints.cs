using Microsoft.EntityFrameworkCore;
using Toimi.Core.Admin;
using Toimi.Core.Configuration;
using Toimi.Core.Data;

namespace Toimi.Web.Admin;

public static class AdminEndpoints
{
  public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
  {
    app.MapGet("/api/admin/summary", async (
        AdminToolsOptions opts, IHttpClientFactory http, string? q, int limit = 0) =>
    {
      var result = await AdminAggregator.AggregateAsync(
          opts.Tools, http, q, limit <= 0 ? 50 : Math.Clamp(limit, 1, 200));
      return Results.Ok(result);
    });

    // Literal route: outranks the /api/admin/{tool}/{**path} template below.
    app.MapGet("/api/admin/usage", async (ToimiDbContext db, ToimiConfiguration config) =>
    {
      var since = DateTimeOffset.UtcNow.AddDays(-30);
      var messages = await db.ConversationMessages
        .Where(m => m.CreatedAt >= since)
        .ToListAsync();
      return Results.Ok(UsageReport.Build(messages, config.TokenPriceInputPer1M, config.TokenPriceOutputPer1M));
    });

    app.Map("/api/admin/{tool}/{**path}", AdminForwarder.ForwardAsync);
  }
}

public record UsageRow(DateOnly Date, long PromptTokens, long CompletionTokens, decimal CostUsd);

public static class UsageReport
{
  public static List<UsageRow> Build(IEnumerable<ConversationMessage> messages, decimal inputPricePer1M, decimal outputPricePer1M)
  {
    return [.. messages
      .GroupBy(m => DateOnly.FromDateTime(m.CreatedAt.UtcDateTime))
      .Select(g =>
      {
        var prompt = g.Sum(m => (long)(m.PromptTokens ?? 0));
        var completion = g.Sum(m => (long)(m.CompletionTokens ?? 0));
        var cost = (prompt / 1_000_000m * inputPricePer1M) + (completion / 1_000_000m * outputPricePer1M);
        return new UsageRow(g.Key, prompt, completion, cost);
      })
      .OrderBy(r => r.Date)];
  }
}

public static class AdminForwarder
{
  // Hop-by-hop headers (RFC 9110 §7.6.1) must not be forwarded by a proxy.
  // HttpClient already de-chunks the upstream body, so forwarding the upstream
  // Transfer-Encoding would advertise framing the body no longer has and hang
  // the client. Kestrel sets the correct framing for the proxied response itself.
  private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
  {
    "Transfer-Encoding", "Connection", "Keep-Alive", "TE",
    "Trailer", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization",
  };

  public static async Task<IResult> ForwardAsync(
      string tool, string? path, HttpContext ctx,
      AdminToolsOptions opts, IHttpClientFactory http)
  {
    if (!opts.Tools.Contains(tool))
    {
      return Results.NotFound();
    }

    var client = http.CreateClient($"admin-{tool}");
    var upstreamPath = $"{AdminRoutes.Base}/{path}{ctx.Request.QueryString}";
    var req = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPath);

    if (ctx.Request.Headers.TryGetValue("If-Unmodified-Since", out var ius))
    {
      req.Headers.TryAddWithoutValidation("If-Unmodified-Since", [.. ius]);
    }

    if (HttpMethods.IsPost(ctx.Request.Method)
        || HttpMethods.IsPut(ctx.Request.Method)
        || HttpMethods.IsPatch(ctx.Request.Method))
    {
      var ms = new MemoryStream();
      await ctx.Request.Body.CopyToAsync(ms);
      ms.Position = 0;
      req.Content = new StreamContent(ms);
      if (!string.IsNullOrEmpty(ctx.Request.ContentType))
      {
        req.Content.Headers.TryAddWithoutValidation("Content-Type", ctx.Request.ContentType);
      }
    }

    HttpResponseMessage resp;
    try { resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead); }
    catch (HttpRequestException ex) { return Results.Problem(ex.Message, statusCode: 502); }

    ctx.Response.StatusCode = (int)resp.StatusCode;
    foreach (var h in resp.Headers)
    {
      if (!HopByHopHeaders.Contains(h.Key))
      {
        ctx.Response.Headers[h.Key] = h.Value.ToArray();
      }
    }

    foreach (var h in resp.Content.Headers)
    {
      if (!HopByHopHeaders.Contains(h.Key))
      {
        ctx.Response.Headers[h.Key] = h.Value.ToArray();
      }
    }

    await resp.Content.CopyToAsync(ctx.Response.Body);
    return Results.Empty;
  }
}

public static class AdminAggregator
{
  public static async Task<AggregatedSummary> AggregateAsync(
      string[] tools, IHttpClientFactory http, string? q, int limit)
  {
    var tasks = tools.Select(async tool =>
    {
      try
      {
        var client = http.CreateClient($"admin-{tool}");
        var rows = await client.GetFromJsonAsync<AdminSummaryDto[]>(
            $"{AdminRoutes.SummaryPath}?q={Uri.EscapeDataString(q ?? string.Empty)}&limit={limit}");
        return (tool, items: (IReadOnlyList<AdminSummaryDto>)(rows ?? []), error: null);
      }
      catch (Exception ex)
      {
        return (tool, items: (IReadOnlyList<AdminSummaryDto>)[], error: (string?)ex.Message);
      }
    });

    var results = await Task.WhenAll(tasks);
    var merged = results
      .SelectMany(r => r.items)
      .OrderByDescending(i => i.UpdatedAt)
      .Take(limit)
      .ToList();
    var errors = results
      .Where(r => r.error is not null)
      .Select(r => new AdminError(r.tool, r.error!))
      .ToList();
    return new AggregatedSummary(merged, errors);
  }
}
