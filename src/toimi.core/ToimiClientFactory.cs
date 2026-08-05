using Microsoft.Extensions.AI;
using AIChatOptions = Microsoft.Extensions.AI.ChatOptions;

namespace Toimi.Core;

public static class ToimiClientFactory
{
  public static AIChatOptions CreateRequestOptions(IReadOnlyList<AITool> tools)
  {
    return new AIChatOptions
    {
      Tools = [.. tools]
    };
  }
}
