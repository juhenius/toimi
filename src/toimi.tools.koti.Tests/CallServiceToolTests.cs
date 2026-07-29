using System.Net;
using System.Text;
using System.Text.Json;
using toimi.tools.koti.HomeAssistant;
using toimi.tools.koti.Tools;
using Xunit;

namespace toimi.tools.koti.Tests;

public class CallServiceToolTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Requests.Add(request);
      Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
      return respond(request);
    }
  }

  private static CallServiceTool Tool(StubHandler handler)
  {
    var client = new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = "http://ha.test:8123", BearerToken = "token" });
    return new CallServiceTool(client);
  }

  private static HttpResponseMessage JsonResponse(string body)
  {
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
  }

  [Theory]
  [InlineData("[1,2]")]
  [InlineData("5")]
  [InlineData("null")]
  [InlineData("\"on\"")]
  public async Task Non_object_data_is_rejected_without_calling_home_assistant(string data)
  {
    var handler = new StubHandler(_ => JsonResponse("[]"));
    var tool = Tool(handler);

    var result = await tool.CallService("light", "turn_on", "light.living_room", data);

    // Valid JSON that is not an object must produce the friendly rejection, not an
    // InvalidOperationException escaping the MCP tool — and HA must never be called.
    Assert.Equal("Invalid JSON in data parameter. Expected a JSON object, e.g. {\"brightness\": 128}.", result);
    Assert.Empty(handler.Requests);
  }

  [Fact]
  public async Task Empty_success_body_still_reports_success()
  {
    // HA returns 200 with an empty body for some service endpoints. By then the
    // device has already acted — reporting failure here is a lie.
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
    var tool = Tool(handler);

    var result = await tool.CallService("light", "turn_on", "light.living_room");

    Assert.Equal("Service called successfully.", result);
  }

  [Fact]
  public async Task Explicit_entity_id_parameter_wins_over_duplicate_in_data()
  {
    var handler = new StubHandler(_ => JsonResponse("[]"));
    var tool = Tool(handler);

    await tool.CallService("light", "turn_on", "light.living_room", /*lang=json,strict*/ """{"entity_id":"light.other","brightness":128}""");

    using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
    Assert.Equal("light.living_room", body.RootElement.GetProperty("entity_id").GetString());
    Assert.Equal(128, body.RootElement.GetProperty("brightness").GetInt32());
    // Exactly one entity_id key — a duplicate lets HA act on whichever wins.
    Assert.Equal(1, body.RootElement.EnumerateObject().Count(p => p.Name == "entity_id"));
  }

  [Fact]
  public async Task Posts_to_the_domain_service_path()
  {
    var handler = new StubHandler(_ => JsonResponse("[]"));
    var tool = Tool(handler);

    await tool.CallService("climate", "set_temperature", "climate.living_room", /*lang=json,strict*/ """{"temperature":22}""");

    var request = Assert.Single(handler.Requests);
    Assert.Equal("/api/services/climate/set_temperature", request.RequestUri!.AbsolutePath);
  }
}
