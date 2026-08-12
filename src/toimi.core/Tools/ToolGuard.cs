using Microsoft.Extensions.Logging;

namespace Toimi.Core.Tools;

/// <summary>
/// The cross-server MCP tool convention: a tool never throws. Failures come
/// back as readable strings the LLM can act on (retry, fix an argument, pick
/// another tool) instead of an opaque MCP protocol error. Wrap the tool body
/// in <see cref="RunAsync"/>; pass <c>translate</c> for the domain-specific
/// pinned messages and let the backstop stringify the rest.
/// </summary>
public static class ToolGuard
{
  /// <summary>
  /// Runs <paramref name="body"/> under the never-throw contract.
  /// <paramref name="translate"/> maps expected domain failures to their
  /// pinned messages (return null to decline); everything untranslated —
  /// including a <paramref name="translate"/> delegate that itself throws —
  /// becomes "{errorPrefix}: {message}" and is logged when a logger is
  /// given. Cancellation is stringified like any other failure (deliberate:
  /// matches the tool-server convention); <c>ResilientMcpTool</c> in this
  /// project differs on purpose by rethrowing <see cref="OperationCanceledException"/>
  /// for the MCP transport path.
  /// </summary>
  public static async Task<string> RunAsync(
    Func<Task<string>> body,
    Func<Exception, string?>? translate = null,
    ILogger? logger = null,
    string errorPrefix = "Error")
  {
    try
    {
      return await body();
    }
#pragma warning disable CA1031 // The backstop IS the convention: MCP tools return readable error strings, never propagate exceptions — including a throwing translate delegate.
    catch (Exception ex)
    {
      try
      {
        if (translate?.Invoke(ex) is { } translated)
        {
          return translated;
        }
      }
      catch (Exception translateEx)
      {
        logger?.LogError(translateEx, "translate delegate failed while handling {OriginalException}", ex.GetType().Name);
        return $"{errorPrefix}: {ex.Message}";
      }

      logger?.LogError(ex, "MCP tool call failed");
      return $"{errorPrefix}: {ex.Message}";
    }
#pragma warning restore CA1031
  }
}
