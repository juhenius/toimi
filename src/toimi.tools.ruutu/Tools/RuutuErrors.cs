using System.Text.Json;

namespace toimi.tools.ruutu.Tools;

/// <summary>
/// Shared ToolGuard failure translations for ruutu's template/content tools
/// (mirrors koti's HomeAssistantErrors). Anything else falls through to
/// ToolGuard's backstop.
/// </summary>
internal static class RuutuErrors
{
  /// <summary>Config/business-rule failure raised by the repository/service layer.</summary>
  public static string? Translate(Exception ex)
  {
    return ex is InvalidOperationException op ? $"Error: {op.Message}" : null;
  }

  /// <summary>A request field failed to parse as JSON; names the offending field.</summary>
  public static string? TranslateJson(Exception ex, string field)
  {
    return ex is JsonException json ? $"Error: {field} is not valid JSON: {json.Message}" : null;
  }
}
