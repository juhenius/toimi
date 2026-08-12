using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.koti.HomeAssistant;
using Toimi.Core.Tools;

namespace toimi.tools.koti.Tools;

[McpServerToolType]
public class GetEntityStateTool(HomeAssistantClient ha)
{
  [McpServerTool, Description("Get the current state of a Home Assistant entity. Returns state (on/off/value), attributes (brightness, rgb_color, temperature, etc.), and last changed time.")]
  public Task<string> GetEntityState(
    [Description("Entity ID (e.g. 'light.living_room', 'sensor.temperature', 'switch.tv')")] string entityId)
  {
    return ToolGuard.RunAsync(async () =>
    {
      var state = await ha.GetStateAsync(entityId);
      return state is null ? "Entity not found." : state.Value.ToString();
    }, translate: HomeAssistantErrors.Translate);
  }
}
