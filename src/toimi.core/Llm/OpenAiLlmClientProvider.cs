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
  public string ResolveModel(ModelTier tier)
  {
    return tier == ModelTier.Smart && !string.IsNullOrWhiteSpace(config.OpenAI.SmartModel)
      ? config.OpenAI.SmartModel
      : config.OpenAI.FastModel;
  }

  public LlmSession Create(ModelTier tier = ModelTier.Fast)
  {
    var options = new OpenAIClientOptions
    {
      NetworkTimeout = TimeSpan.FromSeconds(config.OpenAI.NetworkTimeoutSeconds),
      RetryPolicy = new ClientRetryPolicy(config.OpenAI.MaxRetries),
    };

    var model = ResolveModel(tier);
    var openAiClient = new OpenAIClient(new ApiKeyCredential(config.OpenAI.ApiKey), options);

    // Responses API, not chat completions: reasoning-capable models (gpt-5.x)
    // reject function tools on /v1/chat/completions unless reasoning is forced
    // off, which would waste the smart tier. /v1/responses supports both.
    // OPENAI001: the SDK marks the Responses client experimental, but it is the
    // endpoint OpenAI's own 400 directs these models to — accepted deliberately.
#pragma warning disable OPENAI001
    var inner = openAiClient.GetResponsesClient().AsIChatClient(model);
#pragma warning restore OPENAI001
    var notifier = new ToolCallNotifier(inner);

    var client = new ChatClientBuilder(notifier)
        .UseFunctionInvocation()
        .Build();

    return new LlmSession(client, notifier, model);
  }
}
