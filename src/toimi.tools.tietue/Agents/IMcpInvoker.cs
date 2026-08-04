namespace toimi.tools.tietue.Agents;

public interface IMcpInvoker
{
  /// <summary>Calls one MCP tool by name across the configured servers. Returns the tool's text result, or null if no server exposes the tool.</summary>
  Task<string?> CallToolAsync(string tool, string argsJson, CancellationToken ct = default);
}
