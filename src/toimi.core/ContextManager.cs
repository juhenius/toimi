using Microsoft.Extensions.AI;

namespace Toimi.Core;

public static class ContextManager
{
  private const int MaxEstimatedTokens = 100_000; // GPT-4o has 128k context
  private const int CharsPerToken = 4; // rough estimate
  private const int RecentMessagesToKeep = 10;

  public static int EstimateTokens(List<ChatMessage> messages)
  {
    return messages.Sum(m => (m.Text?.Length ?? 0) / CharsPerToken);
  }

  public static async Task<bool> CompactIfNeeded(
    List<ChatMessage> messages,
    IChatClient client,
    CancellationToken ct = default)
  {
    var estimated = EstimateTokens(messages);
    if (estimated < MaxEstimatedTokens)
    {
      return false;
    }

    // Count system messages at the start (keep them all)
    var systemCount = 0;
    for (var i = 0; i < messages.Count; i++)
    {
      if (messages[i].Role == ChatRole.System)
      {
        systemCount++;
      }
      else
      {
        break;
      }
    }

    // Not enough non-system messages to compact
    var nonSystemCount = messages.Count - systemCount;
    if (nonSystemCount <= RecentMessagesToKeep)
    {
      return false;
    }

    // Messages to summarize: between system messages and the recent ones we keep
    var summarizeCount = nonSystemCount - RecentMessagesToKeep;
    if (summarizeCount < 2)
    {
      return false;
    }

    var toSummarize = messages.GetRange(systemCount, summarizeCount);

    // Build summary prompt
    var conversationText = string.Join("\n\n", toSummarize.Select(m => $"{m.Role}: {m.Text}"));
    var summaryMessages = new List<ChatMessage>
    {
      new(ChatRole.System, "Summarize the following conversation concisely. Preserve key facts, decisions, user preferences, and action items. Be brief but complete."),
      new(ChatRole.User, conversationText)
    };

    var response = await client.GetResponseAsync(summaryMessages, cancellationToken: ct);
    var summary = response.Text ?? "Earlier conversation summary unavailable.";

    // Replace summarized messages with a single system message
    messages.RemoveRange(systemCount, summarizeCount);
    messages.Insert(systemCount, new(ChatRole.System, $"Summary of earlier conversation:\n{summary}"));

    return true;
  }
}
