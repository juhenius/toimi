using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Agents;

public record AgentRunResult(bool Success, string Response, string? ToolCallsJson, string? Error, int? PromptTokens = null, int? CompletionTokens = null);

public interface IAgentRunner
{
  Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default);
}
