using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Toimi.Core;

/// <summary>
/// The conversation transcript as a structured type. Four parts, in snapshot
/// order: a fixed SystemPrompt, a DynamicContext message (regenerated from the
/// stored skill/type catalogs + the clock — never parsed), an optional Summary
/// (written only by compaction), and the Window of exchanges. The old
/// conventions — "index 1 is the dynamic message", "the summary is whatever
/// starts with the magic prefix", "RecordUsage must run before the assistant
/// append" — are unrepresentable here: slots are fields and the budget anchor
/// is taken inside <see cref="AppendAssistant"/>.
/// </summary>
public sealed class ConversationContext
{
  private readonly ChatMessage _systemPrompt;
  private readonly string? _skillSummary;
  private readonly string? _typeCatalog;
  private readonly ContextBudget _budget;
  private readonly TimeProvider _time;
  private readonly List<ChatMessage> _window = [];
  private ChatMessage _dynamicContext;
  private ChatMessage? _summary;

  public ConversationContext(
    string? skillSummary = null, string? typeCatalog = null,
    ContextBudget? budget = null, TimeProvider? timeProvider = null)
  {
    _skillSummary = skillSummary;
    _typeCatalog = typeCatalog;
    _budget = budget ?? new ContextBudget();
    _time = timeProvider ?? TimeProvider.System;
    _systemPrompt = new ChatMessage(ChatRole.System, SystemPrompt);
    _dynamicContext = BuildDynamicContext();
  }

  /// <summary>
  /// Read-only snapshot in slot order: [SystemPrompt, DynamicContext, Summary?,
  /// ...Window]. Built fresh per call — a held reference stays frozen and cannot
  /// be downcast to a mutable list.
  /// </summary>
  public IReadOnlyList<ChatMessage> ToChatMessages()
  {
    var result = new List<ChatMessage>(2 + (_summary is null ? 0 : 1) + _window.Count)
    {
      _systemPrompt,
      _dynamicContext,
    };
    if (_summary is not null)
    {
      result.Add(_summary);
    }

    result.AddRange(_window);
    return result.AsReadOnly();
  }

  public void Append(ChatRole role, string text)
  {
    _window.Add(new ChatMessage(role, text));
  }

  /// <summary>Window append of a pre-built message (e.g. tool-content messages in tests).</summary>
  public void Append(ChatMessage message)
  {
    _window.Add(message);
  }

  public void AppendUser(string text)
  {
    Append(ChatRole.User, text);
  }

  /// <summary>
  /// Appends the assistant response. When the provider reported real usage,
  /// anchors the budget to the transcript AS SENT (i.e. before this append), so
  /// the response counts into the chars-delta and the estimate stays
  /// conservative — the anchor-before-append ordering is internal and cannot be
  /// done wrong by a caller.
  /// </summary>
  public void AppendAssistant(string text, int? promptTokensAsSent = null)
  {
    if (promptTokensAsSent is int promptTokens)
    {
      _budget.RecordUsage(promptTokens, ToChatMessages());
    }

    Append(ChatRole.Assistant, text);
  }

  /// <summary>Regenerates the dynamic context (clock + catalogs) from the stored fields.</summary>
  public void RefreshDynamicContext()
  {
    _dynamicContext = BuildDynamicContext();
  }

  /// <summary>Estimated prompt tokens for the current transcript (budget-anchored when usage was recorded).</summary>
  public int Estimate()
  {
    return _budget.Estimate(ToChatMessages());
  }

  /// <summary>
  /// Removes a trailing assistant message from the window (for hosts whose
  /// persist of the assistant message failed). The slots are structurally out of
  /// reach. Returns false (no-op) when the window is empty or ends elsewhere.
  /// </summary>
  public bool DiscardLastAssistantMessage()
  {
    if (_window.Count > 0 && _window[^1].Role == ChatRole.Assistant)
    {
      _window.RemoveAt(_window.Count - 1);
      return true;
    }

    return false;
  }

  /// <summary>Fresh conversation: clears window + summary, regenerates the dynamic context, clears the budget anchor. Catalogs are kept.</summary>
  public void Reset()
  {
    _window.Clear();
    _summary = null;
    _dynamicContext = BuildDynamicContext();
    _budget.Reset();
  }

  private const int RecentMessagesToKeep = 10;
  private const int MaxToolResultCharsInSummary = 500;
  private const int MaxSummaryInputChars = 300_000;
  private const string SummaryPrefix = "Summary of earlier conversation:";

  // Stable identity and behavior policies. Rarely changes.
  private const string SystemPrompt = """
    You are Toimi, a personal AI assistant for a single user.

    Your role is to help the user think, plan, remember, organize, automate, and act across the tools available to you. You are not a generic chatbot. You are a reliable, calm, action-oriented personal operator that can use tools, follow saved procedures, and build continuity across conversations.

    ## Core identity

    You are proactive but not pushy, concise by default, practical, honest, and focused on getting things done. You are aware that you are operating inside the user's personal system and that your actions may have real-world effects.

    ## Priorities

    1. Help the user accomplish their goal correctly and efficiently.
    2. Use tools when they materially improve correctness, save effort, or enable action.
    3. Preserve continuity by using memory and existing skills when relevant.
    4. Be transparent about what you know, what you inferred, and what you did.
    5. Avoid unnecessary questions when a reasonable assumption is available.

    ## Operating principles

    - Prefer doing over describing. Prefer concrete next steps over abstract advice.
    - If a task can be completed with available tools, do it.
    - Never pretend a tool succeeded if it did not.
    - Never claim to remember something unless it was retrieved from memory or stated in the current context.
    - Never fabricate tool results, device state, schedules, or past actions.
    - When the user's request is ambiguous, proceed with the most reasonable interpretation and say so briefly.
    - If a request has multiple parts, handle all parts.

    ## Communication style

    - Be natural, clear, and efficient. Default to short responses.
    - Avoid filler, hedging, and generic assistant language.
    - Avoid repeating the user's request back to them.
    - After tool use, summarize the outcome in plain language.
    - When relevant, separate what you know, what you inferred, and what you changed.
    - Use Finnish when the user writes in Finnish.

    ## Memory policy

    Memory is for durable user-specific facts, preferences, environment details, and recurring context.

    Store to memory when the user shares something personal, stable, and likely to be useful later. Use source="user" and confirmed=true for user-stated facts. Use source="inferred" and confirmed=false for things you deduced. Set expiresAt for temporary context.

    Do not store one-off transient facts, secrets unless asked, or verbose summaries when a concise fact will do. Keep entries concise, factual, and reusable.

    When memory is relevant, retrieve it before asking the user to repeat themselves.

    ## Creating and updating data

    Before creating an entity, check whether a matching one already exists: search or list by its natural identifier (for example a url or title) and reuse or update that entity instead of creating a duplicate. This matters most across turns — when a later request refers to something you created earlier (for example "set up a price watcher for that item"), find the existing entity and act on it rather than creating a new one.

    ## Skills policy

    Skills are reusable procedures. When a relevant skill exists, prefer using it over reinventing the workflow. Follow the skill faithfully while adapting to the current situation. If you discover a repeatable procedure, consider suggesting it be saved as a skill.

    ## Reminders and scheduling

    Use reminders for things the user wants to be told about later. Reminders automatically send push notifications when due — no need to set up separate notifications. Use scheduled tasks for repeated agentic work (periodic checks, summaries, monitoring).

    Translate natural language into precise timing. Include enough detail that a future prompt is self-contained. Default timezone is Europe/Helsinki. If timing is ambiguous and high-impact, ask.

    ## Smart home

    Prefer correctness and safety over initiative. Confirm state through tools when needed. Interpret room and device requests naturally using the home inventory skill when available. For security-sensitive actions (locks, alarms, heating), be especially careful — ask if intent is unclear.

    Never fabricate device state.

    ## Autonomy

    Take initiative in low-risk situations where intent is clear. Do not chain many impactful actions without the user asking. Prefer reversible actions. The user remains in control.

    ## Safety

    Respect privacy. Minimize unnecessary retention. Do not perform harmful or dangerous actions. For high-risk physical-world instructions, refuse or de-escalate and offer safer alternatives.
    """;

  /// <summary>
  /// Compacts the transcript when the estimate reaches <paramref name="maxTokens"/>:
  /// the prior summary (if any) plus the oldest window messages are summarized via
  /// one LLM call into the Summary slot, keeping the slots, any window-leading
  /// System messages (e.g. a fenced entity payload), and the 10 most recent
  /// exchanges. Fails soft: on summarization error/timeout the transcript is
  /// untouched — an over-budget prompt the provider trims is strictly better than
  /// dropping the user's turn.
  /// </summary>
  public async Task<bool> CompactIfNeededAsync(IChatClient client, int maxTokens = 100_000, CancellationToken ct = default)
  {
    if (Estimate() < maxTokens)
    {
      return false;
    }

    // Window-leading System messages are protected, mirroring the old
    // leading-run rule for host-appended system context.
    var leadingSystem = 0;
    while (leadingSystem < _window.Count && _window[leadingSystem].Role == ChatRole.System)
    {
      leadingSystem++;
    }

    // Same trigger arithmetic as the old ContextManager. The prior summary counts
    // as summarizable content (folded into the new summary), never as protection.
    var summaryCount = _summary is null ? 0 : 1;
    var nonSystemCount = summaryCount + (_window.Count - leadingSystem);
    if (nonSystemCount <= RecentMessagesToKeep)
    {
      return false;
    }

    var summarizeCount = nonSystemCount - RecentMessagesToKeep;
    if (summarizeCount < 2)
    {
      return false;
    }

    var fromWindow = summarizeCount - summaryCount;
    var toSummarize = new List<ChatMessage>();
    if (_summary is not null)
    {
      toSummarize.Add(_summary);
    }

    toSummarize.AddRange(_window.GetRange(leadingSystem, fromWindow));

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
      // Summarization failed/timed out: proceed uncompacted.
      return false;
    }

    _window.RemoveRange(leadingSystem, fromWindow);
    // Deliberate deviation from the old ContextManager: it inserted the summary
    // AFTER any window-leading System messages (protected block); the Summary
    // slot here always sits BEFORE the window in ToChatMessages(), i.e. ahead of
    // leadingSystem — a fixed slot position, not a re-derived insertion index.
    _summary = new ChatMessage(ChatRole.System, $"{SummaryPrefix}\n{summary}");
    _budget.Reset();

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

  private ChatMessage BuildDynamicContext()
  {
    var context = new System.Text.StringBuilder();
    context.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
      $"Current time: {_time.GetUtcNow():yyyy-MM-dd HH:mm} UTC (Europe/Helsinki is UTC+2 or UTC+3 during DST)");

    if (!string.IsNullOrEmpty(_skillSummary))
    {
      context.AppendLine();
      context.AppendLine("Available skills (use GetSkill for full instructions):");
      context.AppendLine(_skillSummary);
    }

    if (!string.IsNullOrEmpty(_typeCatalog))
    {
      context.AppendLine();
      context.AppendLine("Available data types (use create/search/list with these type names; data must match the JSON schema):");
      context.AppendLine(_typeCatalog);
    }

    return new ChatMessage(ChatRole.System, context.ToString());
  }
}
