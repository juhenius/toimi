using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;

namespace Toimi.Core;

/// <summary>
/// Wraps an MCP-discovered AIFunction so that a transport-shaped failure
/// (HTTP error, socket reset, MCP exception, disposed channel — typically
/// because the server pod restarted or the SSE connection was killed
/// during host sleep) triggers a one-shot reconnect-and-retry against the
/// owning server.
///
/// All schema/metadata accessors delegate to the current inner tool, so
/// the chat client sees a stable description that updates transparently
/// after a reconnect.
/// </summary>
internal sealed class ResilientMcpTool(McpToolAggregator aggregator, string serverName, AIFunction initialInner) : AIFunction
{
  private AIFunction _inner = initialInner;
  private readonly string _toolName = initialInner.Name;

  public override string Name => _toolName;
  public override string Description => _inner.Description;
  public override JsonElement JsonSchema => _inner.JsonSchema;
  public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
  public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;
  public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
  public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;

  protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
  {
    var staleInner = _inner;
    try
    {
      return await staleInner.InvokeAsync(arguments, cancellationToken);
    }
    catch (OperationCanceledException)
    {
      throw;
    }
#pragma warning disable CA1031 // Generic catch: we narrow to transport faults via IsTransportFault before retrying
    catch (Exception ex) when (IsTransportFault(ex))
#pragma warning restore CA1031
    {
      Console.Error.WriteLine($"  [{serverName}] Tool '{_toolName}' failed with transport error, reconnecting: {ex.Message}");
      var fresh = await aggregator.ReconnectAndGetToolAsync(serverName, _toolName, staleInner, cancellationToken);
      if (fresh is null)
      {
        Console.Error.WriteLine($"  [{serverName}] Reconnect failed; surfacing original error for '{_toolName}'.");
        throw;
      }

      _inner = fresh;
      Console.WriteLine($"  [{serverName}] Reconnected; retrying '{_toolName}'.");
      return await fresh.InvokeAsync(arguments, cancellationToken);
    }
  }

  private static bool IsTransportFault(Exception ex)
  {
    for (var e = ex; e is not null; e = e.InnerException)
    {
      if (e is HttpRequestException
          or IOException
          or SocketException
          or ObjectDisposedException
          or McpException)
      {
        return true;
      }
    }
    return false;
  }
}
