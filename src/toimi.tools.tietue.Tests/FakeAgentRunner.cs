using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Tests;

public class FakeAgentRunner : IAgentRunner
{
  public List<(Entity Entity, string Prompt)> Runs { get; } = [];
  public AgentRunResult Result { get; set; } = new(true, "ok", null, null);

  public Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default)
  {
    Runs.Add((entity, prompt));
    return Task.FromResult(Result);
  }
}
