using System.Text.Json;

namespace Toimi.Core;

/// <summary>
/// THE single wire shape for tool-call activity JSON. This exact shape is the React
/// client's replay contract (useToimi.ts parses type/CallId/Name/Arguments/Result/
/// DurationMs) and is persisted verbatim into ConversationMessage.ToolCallsJson and
/// into tietue's EntityEvent results. Do not reshape it without migrating both
/// stores and the client parser.
/// </summary>
public static class ToolEventJson
{
  public static string? Serialize(IReadOnlyCollection<TurnUpdate> updates)
  {
    if (updates.Count == 0)
    {
      return null;
    }

    var wire = new List<object>(updates.Count);
    foreach (var update in updates)
    {
      switch (update)
      {
        case ToolCallUpdate tc:
          wire.Add(new { type = "call", tc.CallId, tc.Name, tc.Arguments });
          break;
        case ToolResultUpdate tr:
          wire.Add(new { type = "result", tr.CallId, tr.Result, tr.DurationMs });
          break;
        default:
          break;
      }
    }

    return wire.Count == 0 ? null : JsonSerializer.Serialize(wire);
  }
}
