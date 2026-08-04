using Microsoft.Extensions.AI;

namespace Toimi.Core.Llm;

/// <summary>
/// A constructed LLM pipeline for one session or agent run. Client is the outermost
/// chat client to invoke; Notifier is the ToolCallNotifier the provider embedded
/// BELOW the function-invocation layer, so tool calls and results are observed
/// while the invocation loop runs. The layering is the provider's knowledge —
/// callers only consume the pair.
/// </summary>
public sealed record LlmSession(IChatClient Client, ToolCallNotifier Notifier);

/// <summary>Constructs the chat client + tool-call notifier for a session or agent run.</summary>
public interface ILlmClientProvider
{
  LlmSession Create();
}
