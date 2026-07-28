using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using Toimi.Core.Configuration;

namespace Toimi.Core.Llm;

/// <summary>
/// Builds the OpenAI-backed chat client with an explicit per-request network timeout
/// and a bounded transient-retry policy, so a hung or flaky provider degrades predictably
/// instead of stalling a turn or an agent run indefinitely.
/// </summary>
public sealed class OpenAiLlmClientProvider(ToimiConfiguration config) : ILlmClientProvider
{
  public (IChatClient Client, ToolCallNotifier Notifier) Create()
  {
    var options = new OpenAIClientOptions
    {
      NetworkTimeout = TimeSpan.FromSeconds(config.OpenAI.NetworkTimeoutSeconds),
      RetryPolicy = new ClientRetryPolicy(config.OpenAI.MaxRetries),
    };

    var openAiClient = new OpenAIClient(new ApiKeyCredential(config.OpenAI.ApiKey), options);
    var inner = openAiClient.GetChatClient(config.OpenAI.Model).AsIChatClient();
    var notifier = new ToolCallNotifier(inner);

    var client = new ChatClientBuilder(notifier)
        .UseFunctionInvocation()
        .Build();

    return (client, notifier);
  }
}
