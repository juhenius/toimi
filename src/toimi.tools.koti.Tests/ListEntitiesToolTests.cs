using System.Net;
using System.Text;
using System.Text.Json;
using toimi.tools.koti.HomeAssistant;
using toimi.tools.koti.Tools;
using Xunit;

namespace toimi.tools.koti.Tests;

public class ListEntitiesToolTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(respond(request));
    }
  }

  private const string States = /*lang=json,strict*/ """
    [
      {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen light"}},
      {"entity_id":"light.hall","state":"off","attributes":{}},
      {"entity_id":"sensor.temp","state":"21.5","attributes":{"friendly_name":"Temp"}}
    ]
    """;

  private static HttpResponseMessage Json(string body)
  {
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
  }

  private static HttpResponseMessage Text(string body)
  {
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
  }

  private static ListEntitiesTool Tool(string statesJson, Func<HttpResponseMessage> templateResponse)
  {
    var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
    {
      "/api/states" => Json(statesJson),
      "/api/template" => templateResponse(),
      _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });
    var client = new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = "http://ha.test:8123", BearerToken = "token" });
    return new ListEntitiesTool(client);
  }

  [Fact]
  public async Task Windows_line_endings_in_the_template_response_still_resolve_areas()
  {
    // HA can emit \r\n; a regression that stops trimming \r makes EVERY area
    // lookup miss silently — total, invisible loss of area assignment.
    var tool = Tool(States, () => Text("light.kitchen|Keittiö\r\nlight.hall|\r\nsensor.temp|Olohuone\r\n"));

    var result = await tool.ListEntities();

    using var doc = JsonDocument.Parse(result);
    var byId = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("entity_id").GetString()!);
    Assert.Equal("Keittiö", byId["light.kitchen"].GetProperty("area").GetString());
    Assert.Equal(JsonValueKind.Null, byId["light.hall"].GetProperty("area").ValueKind); // empty area excluded
    Assert.Equal("Olohuone", byId["sensor.temp"].GetProperty("area").GetString());
  }

  [Fact]
  public async Task Template_api_failure_degrades_to_null_areas_when_no_area_filter()
  {
    // Restricted tokens commonly get 403 on /api/template while /api/states works.
    // Listing lights must not die because area resolution is unavailable.
    var tool = Tool(States, () => new HttpResponseMessage(HttpStatusCode.Forbidden));

    var result = await tool.ListEntities(domain: "light");

    using var doc = JsonDocument.Parse(result);
    Assert.Equal(2, doc.RootElement.GetArrayLength());
    Assert.All(doc.RootElement.EnumerateArray(),
      e => Assert.Equal(JsonValueKind.Null, e.GetProperty("area").ValueKind));
  }

  [Fact]
  public async Task Template_api_failure_with_an_area_filter_reports_the_failure()
  {
    // With an area filter, degrading to "no areas" would return [] — indistinguishable
    // from a genuinely empty room. Say what actually happened instead.
    var tool = Tool(States, () => new HttpResponseMessage(HttpStatusCode.Forbidden));

    var result = await tool.ListEntities(area: "Keittiö");

    Assert.DoesNotContain("[", result); // not a JSON listing
    Assert.Contains("Area lookup failed", result);
  }

  [Fact]
  public async Task Malformed_entities_are_skipped_not_fatal()
  {
    const string mixed = /*lang=json,strict*/ """
      [
        {"no_entity_id_here":true},
        {"entity_id":"light.ok","state":"on","attributes":{}},
        {"entity_id":"light.nostate","attributes":{}}
      ]
      """;
    var tool = Tool(mixed, () => Text(""));

    var result = await tool.ListEntities();

    using var doc = JsonDocument.Parse(result);
    var entities = doc.RootElement.EnumerateArray().ToList();
    Assert.Equal(2, entities.Count);
    var byId = entities.ToDictionary(e => e.GetProperty("entity_id").GetString()!);
    Assert.Contains("light.ok", byId.Keys);
    Assert.Contains("light.nostate", byId.Keys);
    Assert.Equal(JsonValueKind.Null, byId["light.nostate"].GetProperty("state").ValueKind);
  }

  [Fact]
  public async Task Non_array_states_response_reports_an_error_instead_of_throwing()
  {
    var tool = Tool(/*lang=json,strict*/ """{"message":"API running."}""", () => Text(""));

    var result = await tool.ListEntities();

    Assert.Contains("Unexpected response", result);
  }

  [Fact]
  public async Task Area_filter_is_case_insensitive_but_exact()
  {
    var tool = Tool(States, () => Text("light.kitchen|Keittiö\nsensor.temp|Olohuone\n"));

    using var lower = JsonDocument.Parse(await tool.ListEntities(area: "keittiö"));
    Assert.Equal("light.kitchen", Assert.Single(lower.RootElement.EnumerateArray().ToList()).GetProperty("entity_id").GetString());

    using var partial = JsonDocument.Parse(await tool.ListEntities(area: "Keit"));
    Assert.Equal(0, partial.RootElement.GetArrayLength());
  }

  [Fact]
  public async Task Exactly_limit_matches_is_not_marked_truncated()
  {
    var tool = Tool(States, () => Text(""));

    var atLimit = await tool.ListEntities(domain: "light", limit: 2);
    Assert.DoesNotContain("[truncated", atLimit);

    var overLimit = await tool.ListEntities(domain: "light", limit: 1);
    Assert.Contains("[truncated at 1 entities", overLimit);
  }
}
