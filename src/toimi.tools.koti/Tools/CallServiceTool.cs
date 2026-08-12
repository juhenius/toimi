using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.koti.HomeAssistant;
using Toimi.Core.Tools;

namespace toimi.tools.koti.Tools;

[McpServerToolType]
public class CallServiceTool(HomeAssistantClient ha)
{
  [McpServerTool, Description("Call a Home Assistant service to control devices. Common examples: light/turn_on, light/turn_off, switch/toggle, climate/set_temperature, automation/trigger.")]
  public async Task<string> CallService(
    [Description("Service domain (e.g. 'light', 'switch', 'climate', 'automation')")] string domain,
    [Description("Service name (e.g. 'turn_on', 'turn_off', 'toggle', 'set_temperature')")] string service,
    [Description("Target entity ID (e.g. 'light.living_room')")] string? entityId = null,
    [Description("Optional JSON service data (e.g. '{\"brightness\": 128}' or '{\"temperature\": 22}')")] string? data = null)
  {
    JsonElement? parsedData = null;
    if (data is not null)
    {
      try
      {
        parsedData = JsonDocument.Parse(data).RootElement;
      }
      catch (JsonException)
      {
        return "Invalid JSON in data parameter. Expected a JSON object, e.g. {\"brightness\": 128}.";
      }

      if (parsedData.Value.ValueKind != JsonValueKind.Object)
      {
        return "Invalid JSON in data parameter. Expected a JSON object, e.g. {\"brightness\": 128}.";
      }
    }

    return await ToolGuard.RunAsync(async () =>
    {
      _ = await ha.CallServiceAsync(domain, service, entityId, parsedData);
      return "Service called successfully.";
    }, translate: HomeAssistantErrors.Translate);
  }
}
