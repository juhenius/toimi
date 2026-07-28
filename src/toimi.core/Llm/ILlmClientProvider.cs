using Microsoft.Extensions.AI;

namespace Toimi.Core.Llm;

/// <summary>Constructs the chat client + tool-call notifier for a session or agent run.</summary>
public interface ILlmClientProvider
{
  (IChatClient Client, ToolCallNotifier Notifier) Create();
}
