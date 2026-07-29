using Toimi.Core.Configuration;
using Xunit;

namespace Toimi.Core.Tests;

public class McpToolAggregatorTests
{
  [Fact]
  public async Task CallToolAsync_returns_null_for_unknown_tool_instead_of_throwing()
  {
    // ToimiHub/AgentRunner feed this straight into CreateInitialMessages: null
    // must mean "degrade gracefully", never an exception that aborts the session.
    var aggregator = new McpToolAggregator();

    Assert.Null(await aggregator.CallToolAsync("list_skills"));
  }

  [Fact]
  public async Task ConnectAllAsync_swallows_unreachable_servers_and_registers_no_tools()
  {
    // One dead MCP pod must not take down every session: each failed connect
    // logs a warning and is skipped.
    var aggregator = new McpToolAggregator();
    var servers = new List<McpServerOptions>
    {
      new() { Name = "bad1", Transport = McpTransportType.Stdio, Command = "/nonexistent-binary-toimi-test" },
      new() { Name = "bad2", Transport = McpTransportType.Stdio, Command = "/another-nonexistent-binary" },
    };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await aggregator.ConnectAllAsync(servers, cts.Token);

    Assert.Empty(aggregator.GetAllTools());
    await aggregator.DisposeAsync();
  }

  [Fact]
  public async Task DisposeAsync_on_a_never_connected_aggregator_is_a_no_op()
  {
    var aggregator = new McpToolAggregator();
    await aggregator.DisposeAsync();
  }
}
