using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.koti.HomeAssistant;

namespace toimi.tools.koti.Tools;

[McpServerToolType]
public class GetHistoryTool(HomeAssistantClient ha)
{
  [McpServerTool, Description("Get the state history of a Home Assistant entity over a time period.")]
  public async Task<string> GetHistory(
    [Description("Entity ID (e.g. 'sensor.temperature')")] string entityId,
    [Description("Number of hours of history to retrieve (default 24, max 168)")] int hours = 24)
  {
    if (hours < 1 || hours > 168)
      return "Hours must be between 1 and 168.";

    var result = await ha.GetHistoryAsync(entityId, hours);
    return result.ToString();
  }
}
