using toimi.tools.tietue.Agents;

namespace toimi.tools.tietue.Tests;

public class FakeMcpInvoker : IMcpInvoker
{
  public List<(string Tool, string ArgsJson)> Calls { get; } = [];
  public string? NextResult { get; set; } = "ok";
  public Exception? NextException { get; set; }

  /// <summary>When set, every call hangs until the token cancels (simulates a hung MCP server).</summary>
  public bool Hang { get; set; }

  public async Task<string?> CallToolAsync(string tool, string argsJson, CancellationToken ct = default)
  {
    Calls.Add((tool, argsJson));
    if (Hang)
    {
      await Task.Delay(Timeout.Infinite, ct);
    }

    return NextException is not null ? throw NextException : NextResult;
  }
}
