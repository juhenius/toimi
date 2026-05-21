using Toimi.Core.Configuration;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Toimi.Core;

public class McpToolAggregator : IAsyncDisposable
{
  private readonly List<McpClient> _clients = [];
  private readonly List<HttpClient> _httpClients = [];
  private readonly List<AITool> _tools = [];

  public async Task ConnectAllAsync(IList<McpServerOptions> servers, CancellationToken cancellationToken = default)
  {
    foreach (var server in servers)
    {
      try
      {
        var client = server.Transport switch
        {
          McpTransportType.Http => await ConnectHttpAsync(server, cancellationToken),
          McpTransportType.Stdio => await ConnectStdioAsync(server, cancellationToken),
          _ => throw new ArgumentException($"Unknown transport type: {server.Transport}")
        };

        _clients.Add(client);

        var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
        _tools.AddRange(tools);

        Console.WriteLine($"  [{server.Name}] Connected, {tools.Count} tools discovered.");
      }
      catch (Exception ex)
      {
        Console.Error.WriteLine($"  [{server.Name}] Failed to connect: {ex.Message}");
      }
    }
  }

  public IList<AITool> GetAllTools()
  {
    return _tools;
  }

  public async Task<string?> CallToolAsync(string toolName, Dictionary<string, object?>? arguments = null, CancellationToken ct = default)
  {
    var tool = _tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == toolName);
    if (tool is null)
    {
      return null;
    }

    var args = arguments is not null
      ? new AIFunctionArguments(arguments)
      : [];

    var result = await tool.InvokeAsync(args, ct);
    return result?.ToString();
  }

  private async Task<McpClient> ConnectHttpAsync(McpServerOptions server, CancellationToken cancellationToken)
  {
    var httpClient = new HttpClient();
    _httpClients.Add(httpClient);

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
    foreach (var client in _clients)
    {
      await client.DisposeAsync();
    }

    foreach (var httpClient in _httpClients)
    {
      httpClient.Dispose();
    }

    GC.SuppressFinalize(this);
  }
}
