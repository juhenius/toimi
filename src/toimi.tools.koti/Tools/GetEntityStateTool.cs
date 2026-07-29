using System.ComponentModel;
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
    try
    {
      var state = await ha.GetStateAsync(entityId);
      return state is null ? "Entity not found." : state.Value.ToString();
    }
    catch (HttpRequestException ex)
    {
      return $"Home Assistant request failed: {ex.Message}";
    }
    catch (TaskCanceledException)
    {
      return "Home Assistant request timed out.";
    }
  }
}
