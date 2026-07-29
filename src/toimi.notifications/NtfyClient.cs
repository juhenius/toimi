using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Toimi.Notifications;

public class NtfyClient(NtfyOptions options, HttpClient? httpClient = null)
{
  private static readonly HttpClient DefaultHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
  private readonly HttpClient _http = httpClient ?? DefaultHttp;
  private const int MaxErrorBodyChars = 500;
  private static readonly Dictionary<string, int> PriorityMap = new(StringComparer.OrdinalIgnoreCase)
  {
    ["min"] = 1,
    ["low"] = 2,
    ["default"] = 3,
    ["high"] = 4,
    ["urgent"] = 5
  };

  public async Task SendAsync(
    string message,
    string? title = null,
    string priority = "default",
    string? tags = null,
    CancellationToken ct = default)
  {
    var url = options.BaseUrl.TrimEnd('/');

    var payload = new Dictionary<string, object>
    {
      ["topic"] = options.Topic,
      ["message"] = message,
      ["priority"] = PriorityMap.GetValueOrDefault(priority, 3)
    };

    if (title is not null)
    {
      payload["title"] = title;
    }

    if (tags is not null)
    {
      payload["tags"] = tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    var request = new HttpRequestMessage(HttpMethod.Post, url)
    {
      Content = new StringContent(
        JsonSerializer.Serialize(payload),
        Encoding.UTF8, "application/json")
    };

    if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.Password))
    {
      var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}"));
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    var response = await _http.SendAsync(request, ct);
    if (!response.IsSuccessStatusCode)
    {
      var body = await response.Content.ReadAsStringAsync(ct);
      // The message ends up in tietue's EntityEvent.Result (jsonb) — cap it so an
      // HTML error page from a proxy cannot flood the event log.
      if (body.Length > MaxErrorBodyChars)
      {
        body = body[..MaxErrorBodyChars] + "… [truncated]";
      }

      throw new HttpRequestException(
        $"ntfy returned {(int)response.StatusCode} ({response.StatusCode}): {body}", null, response.StatusCode);
    }
  }
}
