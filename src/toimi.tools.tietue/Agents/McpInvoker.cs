using System.Text.Json;
using Toimi.Core;
using Toimi.Core.Configuration;

namespace toimi.tools.tietue.Agents;

/// <summary>
/// Connects per call: the effect applier may loop up to its per-run mcpCall cap
/// (10 calls ≈ 50 server connects worst case across the configured servers),
/// all bounded by the handler's effects budget. Session reuse is deliberately
/// deferred — script effects fire at most every scheduler tick, so it isn't yet
/// worth the lifetime management of long-lived MCP sessions here.
/// </summary>
public class McpInvoker(ToimiConfiguration config, ILogger<McpInvoker>? logger = null) : IMcpInvoker
{
  public async Task<string?> CallToolAsync(string tool, string argsJson, CancellationToken ct = default)
  {
    await using var aggregator = new McpToolAggregator(logger);
    await aggregator.ConnectAllAsync(config.McpServers, ct);
    var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson) ?? [];
    return await aggregator.CallToolAsync(tool, args, ct);
  }
}
