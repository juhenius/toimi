using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Scriban;
using Scriban.Runtime;

namespace toimi.tools.ruutu.Rendering;

public static class ScribanRenderer
{
  private const int MaxDepth = 3;

  public static Task<string> RenderAsync(
    string templateName, JsonElement data, string tier,
    IRenderTemplateSource source, CancellationToken ct = default)
  {
    return RenderInternalAsync(templateName, data, tier, source, 0, ct);
  }

  /// <summary>
  /// Template filter: returns the URL only if it is an absolute https URL with a
  /// public, externally-routable host, HTML-escaped for safe use in an attribute.
  /// Anything else (other schemes, loopback/private/internal hosts, malformed,
  /// null) collapses to "about:blank" so it can never break out of the attribute
  /// or aim a display's browser at the local network.
  /// </summary>
  public static string SafeUrl(string? input)
  {
    if (string.IsNullOrWhiteSpace(input)) return "about:blank";
    if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return "about:blank";
    if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase)) return "about:blank";
    if (uri.IsLoopback) return "about:blank";

    var host = uri.DnsSafeHost;
    if (string.IsNullOrEmpty(host)) return "about:blank";

    if (IPAddress.TryParse(host, out var ip))
    {
      if (IsPrivate(ip)) return "about:blank";
    }
    else if (!host.Contains('.'))
    {
      // Single-label hostname (e.g. "router", "localhost") — not externally routable.
      return "about:blank";
    }

    return WebUtility.HtmlEncode(uri.AbsoluteUri);
  }

  private static bool IsPrivate(IPAddress ip)
  {
    if (IPAddress.IsLoopback(ip)) return true;

    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
    {
      if (ip.IsIPv6LinkLocal) return true;
      var b6 = ip.GetAddressBytes();
      if ((b6[0] & 0xFE) == 0xFC) return true;                    // fc00::/7 unique-local
      if (ip.IsIPv4MappedToIPv6) return IsPrivate(ip.MapToIPv4()); // unwrap ::ffff:a.b.c.d
      return false;
    }

    if (ip.AddressFamily == AddressFamily.InterNetwork)
    {
      var b = ip.GetAddressBytes();
      return b[0] == 0                                  // 0.0.0.0/8 (unspecified; localhost on some OSes)
          || b[0] == 10                                 // 10/8
          || b[0] == 127                                // 127/8 loopback
          || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) // 100.64/10 CGNAT (RFC 6598)
          || (b[0] == 169 && b[1] == 254)               // 169.254/16 link-local
          || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)  // 172.16/12
          || (b[0] == 192 && b[1] == 168);              // 192.168/16
    }

    return false;
  }

  private static async Task<string> RenderInternalAsync(
    string name, JsonElement data, string tier,
    IRenderTemplateSource source, int depth, CancellationToken ct)
  {
    if (depth > MaxDepth)
      throw new RenderException($"Template recursion exceeded max depth of {MaxDepth} (at '{name}')");

    var body = await source.GetAsync(name, ct);
    if (body is null)
      throw new RenderException($"Template '{name}' not found");

    var html = tier == "legacy" ? body.LegacyHtml : body.ModernHtml;
    if (string.IsNullOrEmpty(html))
      throw new RenderException($"Template '{name}' has no '{tier}' variant");

    var enriched = await EnrichDataWithSlotsAsync(data, tier, source, depth, ct);

    Template template;
    try { template = Template.Parse(html); }
    catch (Exception ex) { throw new RenderException($"Template '{name}' parse error: {ex.Message}", ex); }
    if (template.HasErrors)
      throw new RenderException($"Template '{name}' parse error: {string.Join("; ", template.Messages)}");

    var scriptObj = new ScriptObject();
    foreach (var (k, v) in enriched) scriptObj[k] = v;
    scriptObj.Import("safe_url", (Func<string?, string>)SafeUrl);
    var context = new TemplateContext { StrictVariables = false };
    context.PushGlobal(scriptObj);

    try { return template.Render(context); }
    catch (Exception ex) { throw new RenderException($"Template '{name}' render error: {ex.Message}", ex); }
  }

  private static async Task<Dictionary<string, object?>> EnrichDataWithSlotsAsync(
    JsonElement data, string tier, IRenderTemplateSource source, int depth, CancellationToken ct)
  {
    var result = new Dictionary<string, object?>();
    if (data.ValueKind != JsonValueKind.Object) return result;

    foreach (var prop in data.EnumerateObject())
    {
      result[prop.Name] = JsonToScalar(prop.Value);

      if (IsSlotRef(prop.Value, out var subName, out var subData))
      {
        var subHtml = await RenderInternalAsync(subName!, subData, tier, source, depth + 1, ct);
        result[$"{prop.Name}_html"] = subHtml;
      }
      else if (prop.Value.ValueKind == JsonValueKind.Array)
      {
        var rendered = new List<string>();
        var anySlot = false;
        foreach (var item in prop.Value.EnumerateArray())
        {
          if (IsSlotRef(item, out var iName, out var iData))
          {
            anySlot = true;
            rendered.Add(await RenderInternalAsync(iName!, iData, tier, source, depth + 1, ct));
          }
        }
        if (anySlot) result[$"{prop.Name}_html"] = rendered;
      }
    }
    return result;
  }

  private static bool IsSlotRef(JsonElement v, out string? name, out JsonElement data)
  {
    name = null; data = default;
    if (v.ValueKind != JsonValueKind.Object) return false;
    if (!v.TryGetProperty("template", out var tEl) || tEl.ValueKind != JsonValueKind.String) return false;
    if (!v.TryGetProperty("data", out var dEl)) return false;
    name = tEl.GetString();
    data = dEl;
    return !string.IsNullOrEmpty(name);
  }

  private static object? JsonToScalar(JsonElement v) => v.ValueKind switch
  {
    JsonValueKind.String => v.GetString(),
    JsonValueKind.Number => v.TryGetInt64(out var n) ? n : v.GetDouble(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.Null => null,
    JsonValueKind.Array => v.EnumerateArray().Select(JsonToScalar).ToList(),
    JsonValueKind.Object => v.EnumerateObject().ToDictionary(p => p.Name, p => JsonToScalar(p.Value)),
    _ => null
  };
}
