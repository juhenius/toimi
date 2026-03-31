using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.koti.HomeAssistant;

namespace toimi.tools.koti.Tools;

[McpServerToolType]
public class GetEntityStateTool(HomeAssistantClient ha)
{
  [McpServerTool, Description("Get the current state of a Home Assistant entity. Returns state (on/off/value), attributes (brightness, rgb_color, temperature, etc.), and last changed time.")]
  public async Task<string> GetEntityState(
    [Description("Entity ID (e.g. 'light.living_room', 'sensor.temperature', 'switch.tv')")] string entityId)
  {
    var state = await ha.GetStateAsync(entityId);
    if (state is null)
      return "Entity not found.";

    return state.Value.ToString();
  }
}
