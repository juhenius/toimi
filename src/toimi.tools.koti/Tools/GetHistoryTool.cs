using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.koti.HomeAssistant;
using Toimi.Core.Tools;

namespace toimi.tools.koti.Tools;

[McpServerToolType]
public class GetHistoryTool(HomeAssistantClient ha)
{
  [McpServerTool, Description("Get the state history of a Home Assistant entity over a time period.")]
  public async Task<string> GetHistory(
    [Description("Entity ID (e.g. 'sensor.temperature')")] string entityId,
    [Description("Number of hours of history to retrieve (default 24, max 168)")] int hours = 24)
  {
    return hours is < 1 or > 168
      ? "Hours must be between 1 and 168."
      : await ToolGuard.RunAsync(async () =>
    {
      var result = await ha.GetHistoryAsync(entityId, hours);
      var json = result.GetRawText();
      const int maxChars = 50_000;
      return json.Length <= maxChars
        ? json
        : json[..maxChars] + "\n[truncated — request fewer hours]";
    }, translate: HomeAssistantErrors.Translate);
  }
}
