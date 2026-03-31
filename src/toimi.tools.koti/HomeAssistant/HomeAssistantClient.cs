using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace toimi.tools.koti.HomeAssistant;

public class HomeAssistantClient
{
  private readonly HttpClient _http;

  public HomeAssistantClient(HttpClient http, HomeAssistantOptions options)
  {
    _http = http;
    _http.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");
    var token = options.BearerToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
      ? options.BearerToken["Bearer ".Length..]
      : options.BearerToken;
    _http.DefaultRequestHeaders.Authorization =
      new AuthenticationHeaderValue("Bearer", token);
  }

  public async Task<JsonElement> GetStatesAsync(CancellationToken ct = default)
  {
    var response = await _http.GetAsync("api/states", ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return JsonDocument.Parse(json).RootElement;
  }

  public async Task<JsonElement?> GetStateAsync(string entityId, CancellationToken ct = default)
  {
    var response = await _http.GetAsync($"api/states/{entityId}", ct);
    if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
      return null;
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return JsonDocument.Parse(json).RootElement;
  }

  public async Task<JsonElement> CallServiceAsync(
    string domain, string service,
    string? entityId = null,
    JsonElement? data = null,
    CancellationToken ct = default)
  {
    using var doc = new MemoryStream();
    using (var writer = new Utf8JsonWriter(doc))
    {
      writer.WriteStartObject();
      if (entityId is not null)
      {
        writer.WriteString("entity_id", entityId);
      }
      if (data is not null)
      {
        foreach (var prop in data.Value.EnumerateObject())
        {
          prop.WriteTo(writer);
        }
      }
      writer.WriteEndObject();
    }

    var content = new StringContent(
      System.Text.Encoding.UTF8.GetString(doc.ToArray()),
      Encoding.UTF8, "application/json");

    var response = await _http.PostAsync($"api/services/{domain}/{service}", content, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return JsonDocument.Parse(json).RootElement;
  }

  public async Task<string> RenderTemplateAsync(string template, CancellationToken ct = default)
  {
    var content = new StringContent(
      JsonSerializer.Serialize(new { template }),
      Encoding.UTF8, "application/json");

    var response = await _http.PostAsync("api/template", content, ct);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync(ct);
  }

  public async Task<Dictionary<string, string>> GetEntityAreasAsync(CancellationToken ct = default)
  {
    var template = """
      {% for state in states %}
      {{ state.entity_id }}|{{ area_name(state.entity_id) or '' }}
      {% endfor %}
      """;

    var result = await RenderTemplateAsync(template, ct);
    var areas = new Dictionary<string, string>();

    foreach (var line in result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
      var parts = line.Split('|', 2);
      if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1]))
        areas[parts[0]] = parts[1];
    }

    return areas;
  }

  public async Task<JsonElement> GetHistoryAsync(
    string entityId,
    int hours = 24,
    CancellationToken ct = default)
  {
    var start = DateTimeOffset.UtcNow.AddHours(-hours);
    var url = $"api/history/period/{start:o}?filter_entity_id={entityId}&minimal_response&no_attributes";

    var response = await _http.GetAsync(url, ct);
    response.EnsureSuccessStatusCode();
    var json = await response.Content.ReadAsStringAsync(ct);
    return JsonDocument.Parse(json).RootElement;
  }
}
