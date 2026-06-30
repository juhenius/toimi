using System.Text.Json;
using Microsoft.Extensions.AI;
using Toimi.Core;
using Toimi.Core.Configuration;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Agents;

public class AgentRunner(ToimiConfiguration config) : IAgentRunner
{
  public async Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default)
  {
    try
    {
      await using var aggregator = new McpToolAggregator();
      await aggregator.ConnectAllAsync(config.McpServers, ct);
      var tools = aggregator.GetAllTools();

      var skillSummary = await aggregator.CallToolAsync("list_skills", ct: ct);
      var typeCatalog = await aggregator.CallToolAsync("list_types", ct: ct);

      var (client, notifier) = ToimiClientFactory.Create(config);
      var options = ToimiClientFactory.CreateRequestOptions(tools);
      var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);

      messages.Add(new(ChatRole.System,
        $"You are acting on a '{entity.Type}' entity (id {entity.Id}). Its current data is:\n{entity.Data.RootElement.GetRawText()}\n" +
        "Use the tietue tools (create/update/search/set_trigger/...) to act on it; you may schedule your own next run with set_trigger on this entity id."));
      messages.Add(new(ChatRole.User, prompt));

      ToimiClientFactory.RefreshDynamicContext(messages);
      await ContextManager.CompactIfNeeded(messages, client, ct);

      var response = await client.GetResponseAsync(messages, options, ct);
      var responseText = response.Text ?? "";

      var toolCalls = new List<object>();
      while (notifier.TryDequeueEvent(out var evt))
      {
        toolCalls.Add(evt!);
      }

      var toolCallsJson = toolCalls.Count > 0 ? JsonSerializer.Serialize(toolCalls) : null;
      return new AgentRunResult(true, responseText, toolCallsJson, null);
    }
    catch (Exception ex)
    {
      return new AgentRunResult(false, "", null, ex.Message);
    }
  }
}
