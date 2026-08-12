namespace toimi.tools.koti.Tools;

/// <summary>
/// The pinned Home Assistant failure translations shared by all four tools
/// (koti.Tests/ToolErrorHandlingTests pins both messages). Anything else falls
/// through to ToolGuard's backstop.
/// </summary>
internal static class HomeAssistantErrors
{
  public static string? Translate(Exception ex)
  {
    return ex switch
    {
      HttpRequestException http => $"Home Assistant request failed: {http.Message}",
      TaskCanceledException => "Home Assistant request timed out.",
      _ => null,
    };
  }
}
