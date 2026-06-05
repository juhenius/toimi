using Toimi.Core.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Toimi.Core;

public class McpToolAggregator : IAsyncDisposable
{
  private readonly Dictionary<string, ServerConnection> _connections = new();
  private readonly Dictionary<string, SemaphoreSlim> _reconnectLocks = new();
  private readonly List<AITool> _wrappedTools = new();

  private sealed record ServerConnection(
    McpServerOptions Options,
    McpClient Client,
    HttpClient? HttpClient,
    Dictionary<string, AIFunction> Tools);

  public async Task ConnectAllAsync(IList<McpServerOptions> servers, CancellationToken cancellationToken = default)
  {
    foreach (var server in servers)
    {
      var connection = await ConnectOneAsync(server, cancellationToken);
      if (connection is null) continue;
      foreach (var tool in connection.Tools.Values)
      {
        _wrappedTools.Add(new ResilientMcpTool(this, server.Name, tool));
      }
    }
  }

  public IList<AITool> GetAllTools()
  {
    return _wrappedTools;
  }

  public async Task<string?> CallToolAsync(string toolName, Dictionary<string, object?>? arguments = null, CancellationToken ct = default)
  {
    var tool = _wrappedTools.OfType<AIFunction>().FirstOrDefault(t => t.Name == toolName);
    if (tool is null) return null;

    var args = arguments is not null ? new AIFunctionArguments(arguments) : [];
    var result = await tool.InvokeAsync(args, ct);
    return result?.ToString();
  }

  /// <summary>
  /// Tears down the existing connection for <paramref name="serverName"/> and rebuilds it.
  /// Returns the freshly-discovered tool with the given name, or null if reconnect failed
  /// or the tool no longer exists on the server. Concurrent callers for the same server
  /// are serialized so we don't open multiple replacement connections.
  /// </summary>
  internal async Task<AIFunction?> ReconnectAndGetToolAsync(string serverName, string toolName, AIFunction staleInner, CancellationToken ct)
  {
    var sem = GetReconnectLock(serverName);
    await sem.WaitAsync(ct);
    try
    {
      // If a parallel caller already swapped the underlying tool, just return it — no need to reconnect again.
      if (_connections.TryGetValue(serverName, out var current) &&
          current.Tools.TryGetValue(toolName, out var existing) &&
          !ReferenceEquals(existing, staleInner))
      {
        return existing;
      }

      if (!_connections.TryGetValue(serverName, out var stale))
      {
        return null;
      }

      var options = stale.Options;
      try { await stale.Client.DisposeAsync(); } catch { /* best-effort cleanup */ }
      stale.HttpClient?.Dispose();
      _connections.Remove(serverName);

      var fresh = await ConnectOneAsync(options, ct);
      if (fresh is null) return null;
      return fresh.Tools.TryGetValue(toolName, out var tool) ? tool : null;
    }
    finally
    {
      sem.Release();
    }
  }

  private SemaphoreSlim GetReconnectLock(string serverName)
  {
    lock (_reconnectLocks)
    {
      if (!_reconnectLocks.TryGetValue(serverName, out var sem))
      {
        sem = new SemaphoreSlim(1, 1);
        _reconnectLocks[serverName] = sem;
      }
      return sem;
    }
  }

  private async Task<ServerConnection?> ConnectOneAsync(McpServerOptions server, CancellationToken cancellationToken)
  {
    try
    {
      McpClient client;
      HttpClient? httpClient = null;

      switch (server.Transport)
      {
        case McpTransportType.Http:
          httpClient = new HttpClient();
          client = await ConnectHttpAsync(server, httpClient, cancellationToken);
          break;
        case McpTransportType.Stdio:
          client = await ConnectStdioAsync(server, cancellationToken);
          break;
        default:
          throw new ArgumentException($"Unknown transport type: {server.Transport}");
      }

      var rawTools = await client.ListToolsAsync(cancellationToken: cancellationToken);
      var toolMap = new Dictionary<string, AIFunction>();
      foreach (var t in rawTools.OfType<AIFunction>())
      {
        toolMap[t.Name] = t;
      }

      var connection = new ServerConnection(server, client, httpClient, toolMap);
      _connections[server.Name] = connection;

      Console.WriteLine($"  [{server.Name}] Connected, {toolMap.Count} tools discovered.");
      return connection;
    }
    catch (Exception ex)
    {
      Console.Error.WriteLine($"  [{server.Name}] Failed to connect: {ex.Message}");
      return null;
    }
  }

  private static async Task<McpClient> ConnectHttpAsync(McpServerOptions server, HttpClient httpClient, CancellationToken cancellationToken)
  {
    var transportOptions = new HttpClientTransportOptions
    {
      Endpoint = new Uri(server.Url!)
    };

    if (server.Headers is { Count: > 0 })
    {
      transportOptions.AdditionalHeaders = new Dictionary<string, string>(server.Headers);
    }

    var transport = new HttpClientTransport(transportOptions, httpClient, loggerFactory: null, ownsHttpClient: false);

    return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
  }

  private static async Task<McpClient> ConnectStdioAsync(McpServerOptions server, CancellationToken cancellationToken)
  {
    var transportOptions = new StdioClientTransportOptions
    {
      Command = server.Command!,
      Arguments = server.Args is { Count: > 0 } ? new List<string>(server.Args) : null
    };

    if (server.Env is { Count: > 0 })
    {
      transportOptions.EnvironmentVariables = new Dictionary<string, string?>(server.Env!);
    }

    var transport = new StdioClientTransport(transportOptions);

    return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
  }

  public async ValueTask DisposeAsync()
  {
    foreach (var conn in _connections.Values)
    {
      try { await conn.Client.DisposeAsync(); } catch { /* best-effort */ }
      conn.HttpClient?.Dispose();
    }
    _connections.Clear();

    lock (_reconnectLocks)
    {
      foreach (var sem in _reconnectLocks.Values) sem.Dispose();
      _reconnectLocks.Clear();
    }

    GC.SuppressFinalize(this);
  }
}
