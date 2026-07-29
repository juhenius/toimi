using System.Net;
using toimi.tools.koti.HomeAssistant;
using toimi.tools.koti.Tools;
using Xunit;

namespace toimi.tools.koti.Tests;

// Every tool wraps its HomeAssistantClient call(s) so that an unreachable or erroring
// Home Assistant instance surfaces a readable string to the LLM instead of an
// exception escaping the MCP tool boundary (mirrors verkko's SendNotificationTool).
public class ToolErrorHandlingTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(respond(request));
    }
  }

  private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      throw ex;
    }
  }

  private const string BearerToken = "super-secret-token";

  private static HomeAssistantClient ClientFor(HttpMessageHandler handler)
  {
    return new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = "http://ha.test:8123", BearerToken = BearerToken });
  }

  [Fact]
  public async Task GetEntityState_surfaces_a_friendly_string_on_401_without_leaking_the_token()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
    var tool = new GetEntityStateTool(ClientFor(handler));

    var result = await tool.GetEntityState("light.living_room");

    Assert.DoesNotContain(BearerToken, result);
    Assert.Contains("Home Assistant", result);
  }

  [Fact]
  public async Task GetEntityState_404_still_reports_entity_not_found()
  {
    // The 404 -> null mapping in HomeAssistantClient must survive the hardening.
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
    var tool = new GetEntityStateTool(ClientFor(handler));

    var result = await tool.GetEntityState("light.missing");

    Assert.Equal("Entity not found.", result);
  }

  [Fact]
  public async Task GetHistory_surfaces_a_friendly_string_when_the_request_throws()
  {
    var handler = new ThrowingHandler(new HttpRequestException("boom"));
    var tool = new GetHistoryTool(ClientFor(handler));

    var result = await tool.GetHistory("sensor.temperature");

    Assert.Contains("Home Assistant", result);
    Assert.Contains("boom", result);
  }

  [Fact]
  public async Task CallService_surfaces_a_friendly_string_on_500()
  {
    var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
    var tool = new CallServiceTool(ClientFor(handler));

    var result = await tool.CallService("light", "turn_on", "light.living_room");

    Assert.Contains("Home Assistant", result);
  }

  [Fact]
  public async Task ListEntities_surfaces_a_friendly_string_when_GetStatesAsync_throws()
  {
    var handler = new ThrowingHandler(new HttpRequestException("states unreachable"));
    var tool = new ListEntitiesTool(ClientFor(handler));

    var result = await tool.ListEntities();

    Assert.Contains("Home Assistant", result);
  }

  [Fact]
  public async Task GetEntityState_reports_a_timeout_distinctly()
  {
    var handler = new ThrowingHandler(new TaskCanceledException());
    var tool = new GetEntityStateTool(ClientFor(handler));

    var result = await tool.GetEntityState("light.living_room");

    Assert.Contains("timed out", result);
  }
}
