using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Toimi.Core;

public static class ContextManager
{
  private const int RecentMessagesToKeep = 10;
  private const int MaxToolResultCharsInSummary = 500;
  private const int MaxSummaryInputChars = 300_000;
  private const string SummaryPrefix = "Summary of earlier conversation:";

  public static async Task<bool> CompactIfNeeded(
    List<ChatMessage> messages,
    IChatClient client,
    ContextBudget? budget = null,
    int maxTokens = 100_000,
    CancellationToken ct = default)
  {
    var estimated = budget?.Estimate(messages) ?? (ContextBudget.TotalChars(messages) / 4);
    if (estimated < maxTokens)
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

    // Prior compaction summaries are System messages sitting at the end of the
    // protected block. Treat them as summarizable content, not protection —
    // otherwise each compaction adds one more permanent summary and the
    // reclaimable window shrinks every cycle.
    while (systemCount > 0 && (messages[systemCount - 1].Text?.StartsWith(SummaryPrefix, StringComparison.Ordinal) ?? false))
    {
      systemCount--;
    }

    var nonSystemCount = messages.Count - systemCount;
    if (nonSystemCount <= RecentMessagesToKeep)
    {
      return false;
    }

    var summarizeCount = nonSystemCount - RecentMessagesToKeep;
    if (summarizeCount < 2)
    {
      return false;
    }

    var toSummarize = messages.GetRange(systemCount, summarizeCount);
    var conversationText = string.Join("\n\n", toSummarize.Select(MessageAsText));
    if (conversationText.Length > MaxSummaryInputChars)
    {
      conversationText = conversationText[..MaxSummaryInputChars] + "\n\n[remainder truncated]";
    }

    var summaryMessages = new List<ChatMessage>
    {
      new(ChatRole.System, "Summarize the following conversation concisely. Preserve key facts, decisions, user preferences, action items, and the outcomes of tool calls. Be brief but complete."),
      new(ChatRole.User, conversationText)
    };

    string summary;
    try
    {
      using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      summaryCts.CancelAfter(TimeSpan.FromSeconds(30));
      var response = await client.GetResponseAsync(summaryMessages, cancellationToken: summaryCts.Token);
      summary = response.Text ?? "Earlier conversation summary unavailable.";
    }
    catch (Exception)
    {
      // Summarization failed/timed out: proceed uncompacted. An over-budget prompt the
      // provider trims is strictly better than dropping the user's turn.
      return false;
    }

    messages.RemoveRange(systemCount, summarizeCount);
    messages.Insert(systemCount, new(ChatRole.System, $"{SummaryPrefix}\n{summary}"));
    budget?.Reset();

    return true;
  }

  private static string MessageAsText(ChatMessage m)
  {
    var parts = new List<string>();
    foreach (var content in m.Contents)
    {
      switch (content)
      {
        case TextContent t when !string.IsNullOrEmpty(t.Text):
          parts.Add(t.Text);
          break;
        case FunctionCallContent fc:
          parts.Add($"[tool call: {fc.Name}({JsonSerializer.Serialize(fc.Arguments)})]");
          break;
        case FunctionResultContent fr:
          var result = fr.Result?.ToString() ?? "";
          if (result.Length > MaxToolResultCharsInSummary)
          {
            result = result[..MaxToolResultCharsInSummary] + "…";
          }

          parts.Add($"[tool result: {result}]");
          break;
        default:
          break;
      }
    }

    return $"{m.Role}: {string.Join("\n", parts)}";
  }
}
