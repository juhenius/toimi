using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Toimi.Core;

/// <summary>
/// Per-session/run token estimator anchored to real usage. After each LLM call the
/// host records the actual prompt-token count; estimates become
/// anchor + charsAddedSince/3 (conservative) instead of blind chars/4.
/// </summary>
public class ContextBudget
{
  private int? _anchorPromptTokens;
  private int _charsAtAnchor;

  public void RecordUsage(int promptTokens, IReadOnlyList<ChatMessage> messages)
  {
    _anchorPromptTokens = promptTokens;
    _charsAtAnchor = TotalChars(messages);
  }

  public int Estimate(IReadOnlyList<ChatMessage> messages)
  {
    var chars = TotalChars(messages);
    if (_anchorPromptTokens is null)
    {
      return chars / 4;
    }

    var delta = Math.Max(0, chars - _charsAtAnchor);
    return _anchorPromptTokens.Value + (delta / 3);
  }

  /// <summary>Call after compaction: the message list changed shape, the anchor is invalid.</summary>
  public void Reset()
  {
    _anchorPromptTokens = null;
    _charsAtAnchor = 0;
  }

  public static int TotalChars(IReadOnlyList<ChatMessage> messages)
  {
    return messages.Sum(MessageChars);
  }

  private static int MessageChars(ChatMessage m)
  {
    var total = 0;
    foreach (var content in m.Contents)
    {
      total += content switch
      {
        TextContent t => t.Text?.Length ?? 0,
        FunctionCallContent fc => fc.Name.Length + (fc.Arguments is null ? 0 : JsonSerializer.Serialize(fc.Arguments).Length),
        FunctionResultContent fr => fr.Result?.ToString()?.Length ?? 0,
        _ => 0,
      };
    }

    return total;
  }
}
