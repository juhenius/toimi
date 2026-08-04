using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Toimi.Core.Configuration;
using Toimi.Core.Llm;

namespace Toimi.Core;

/// <summary>
/// One conversation session with the Toimi agent. Owns the MCP aggregator, the LLM
/// client + tool-call notifier, the transcript, and the context budget; runs the
/// full turn (refresh dynamic context, compact, stream, drain tool events, extract
/// usage, anchor budget, append transcript). Hosts are transports: they forward
/// <see cref="TurnUpdate"/>s and persist what <see cref="TurnCompleted"/> reports.
/// </summary>
public sealed class ToimiAgent : IAsyncDisposable
{
  private readonly ToimiConfiguration _config;
  private readonly McpToolAggregator _aggregator;
  private readonly IChatClient _client;
  private readonly ToolCallNotifier _notifier;
  private readonly ChatOptions _options;
  private readonly List<ChatMessage> _messages;
  private readonly ContextBudget _budget;
  private bool _turnInProgress;

  public int ToolCount { get; }
  public string? SkillSummary { get; }
  public string? TypeCatalog { get; }
  public IReadOnlyList<ChatMessage> Messages => _messages;

  private ToimiAgent(
    ToimiConfiguration config, McpToolAggregator aggregator, LlmSession llm, ChatOptions options,
    List<ChatMessage> messages, string? skillSummary, string? typeCatalog, ContextBudget budget, int toolCount)
  {
    _config = config;
    _aggregator = aggregator;
    _client = llm.Client;
    _notifier = llm.Notifier;
    _options = options;
    _messages = messages;
    _budget = budget;
    SkillSummary = skillSummary;
    TypeCatalog = typeCatalog;
    ToolCount = toolCount;
  }

  /// <summary>
  /// Bootstraps a session: connects all configured MCP servers, discovers tools,
  /// fetches the skill/type catalogs, builds the LLM pipeline, and assembles the
  /// initial system messages. Owns the aggregator it creates — disposed here on
  /// bootstrap failure, otherwise in <see cref="DisposeAsync"/>.
  /// </summary>
  public static async Task<ToimiAgent> StartAsync(
    ToimiConfiguration config, ILlmClientProvider llmProvider,
    ContextBudget? budget = null, ILogger? logger = null, CancellationToken ct = default)
  {
    var aggregator = new McpToolAggregator(logger);
    try
    {
      await aggregator.ConnectAllAsync(config.McpServers, ct);
      var tools = aggregator.GetAllTools();
      var skillSummary = await aggregator.CallToolAsync("list_skills", ct: ct);
      var typeCatalog = await aggregator.CallToolAsync("list_types", ct: ct);
      var llm = llmProvider.Create();
      var options = ToimiClientFactory.CreateRequestOptions(tools);
      var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);
      return new ToimiAgent(config, aggregator, llm, options, messages, skillSummary, typeCatalog, budget ?? new ContextBudget(), tools.Count);
    }
    catch
    {
      await aggregator.DisposeAsync();
      throw;
    }
  }

  /// <summary>
  /// Appends a message without running a turn: history replay (user/assistant) or
  /// extra system context (e.g. a fenced entity payload for a headless run).
  /// </summary>
  public void AppendMessage(ChatRole role, string text)
  {
    _messages.Add(new(role, text));
  }

  /// <summary>
  /// Runs one conversation turn. Contract: the user message is appended first and
  /// STAYS in the transcript on failure (hosts persist it before calling); the
  /// assistant message is appended only after the stream completes, so a mid-stream
  /// failure leaves no phantom assistant context. The stream ends with exactly one
  /// <see cref="TurnCompleted"/> — or throws.
  /// <para>
  /// The returned sequence must be enumerated exactly once. It is cold (nothing
  /// runs until enumeration starts) and single-use: a second enumeration — of the
  /// same call, sequential or concurrent — throws <see cref="InvalidOperationException"/>
  /// without touching the transcript. Start a new turn with a fresh call instead.
  /// </para>
  /// </summary>
  public IAsyncEnumerable<TurnUpdate> SendAsync(string userText, CancellationToken ct = default)
  {
    return new SingleUseTurn(this, userText, ct);
  }

  /// <summary>
  /// Wraps <see cref="SendAsyncCore"/> so the sequence returned by <see cref="SendAsync"/>
  /// can be enumerated at most once. A plain async-iterator method is "cold and
  /// re-enterable": each call to <c>GetAsyncEnumerator</c> beyond the first clones a
  /// fresh state machine and reruns the method body from the top — re-appending the
  /// user message and re-issuing the LLM call against the same transcript. This
  /// wrapper turns a second enumeration into an immediate throw instead.
  /// </summary>
  private sealed class SingleUseTurn(ToimiAgent agent, string userText, CancellationToken ct) : IAsyncEnumerable<TurnUpdate>
  {
    private int _consumed;

    public IAsyncEnumerator<TurnUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
      return Interlocked.Exchange(ref _consumed, 1) != 0
        ? throw new InvalidOperationException("SendAsync's result must be enumerated exactly once; call SendAsync again to start a new turn.")
        : agent.SendAsyncCore(userText, ct).GetAsyncEnumerator(cancellationToken);
    }
  }

  private async IAsyncEnumerable<TurnUpdate> SendAsyncCore(string userText, [EnumeratorCancellation] CancellationToken ct = default)
  {
    // Belt-and-braces against two DIFFERENT SendAsync calls being enumerated
    // concurrently on the same agent — SingleUseTurn only guards re-enumeration of
    // one call's own sequence.
    if (_turnInProgress)
    {
      throw new InvalidOperationException("A turn is already in progress; SendAsync must be enumerated exactly once.");
    }

    _turnInProgress = true;
    try
    {
      _messages.Add(new(ChatRole.User, userText));

      ToimiClientFactory.RefreshDynamicContext(_messages);

      // A summarization failure degrades gracefully inside CompactIfNeeded; anything
      // it does throw propagates to the host with the transcript unchanged past the
      // user message.
      await ContextManager.CompactIfNeeded(_messages, _client, _budget, _config.MaxContextTokens, ct);

      var fullResponse = new StringBuilder();
      var toolEvents = new List<object>();
      UsageDetails? usage = null;

      await foreach (var update in _client.GetStreamingResponseAsync(_messages, _options, ct))
      {
        foreach (var toolUpdate in DrainToolEvents(toolEvents))
        {
          yield return toolUpdate;
        }

        foreach (var content in update.Contents)
        {
          if (content is TextContent textContent)
          {
            fullResponse.Append(textContent.Text);
            yield return new TokenUpdate(textContent.Text);
          }

          if (content is UsageContent usageContent)
          {
            usage = usageContent.Details;
          }
        }
      }

      // Drain any remaining events after streaming completes.
      foreach (var toolUpdate in DrainToolEvents(toolEvents))
      {
        yield return toolUpdate;
      }

      var responseText = fullResponse.ToString();

      // Anchor the budget to the real prompt-token count of the messages AS SENT.
      // The assistant response (appended below) then counts into the chars-delta,
      // keeping the estimate conservative rather than undercounting by one response.
      if (usage?.InputTokenCount is not null)
      {
        _budget.RecordUsage((int)usage.InputTokenCount.Value, _messages);
      }

      _messages.Add(new(ChatRole.Assistant, responseText));

      // Prefer real usage from the final streaming update; fall back to the same
      // rough estimates the web host has always persisted.
      var promptTokens = (int?)usage?.InputTokenCount ?? (ContextBudget.TotalChars(_messages) / 4);
      var completionTokens = (int?)usage?.OutputTokenCount ?? (responseText.Length / 4);
      var totalTokens = (int?)usage?.TotalTokenCount ?? (promptTokens + completionTokens);

      yield return new TurnCompleted(responseText, ToolEventJson.Serialize(toolEvents), promptTokens, completionTokens, totalTokens);
    }
    finally
    {
      _turnInProgress = false;
    }
  }

  /// <summary>Non-streaming convenience for headless callers: runs the turn, returns the terminal update.</summary>
  public async Task<TurnCompleted> RunTurnAsync(string userText, CancellationToken ct = default)
  {
    TurnCompleted? completed = null;
    await foreach (var update in SendAsync(userText, ct))
    {
      if (update is TurnCompleted c)
      {
        completed = c;
      }
    }

    // SendAsync either throws or terminates with TurnCompleted.
    return completed!;
  }

  /// <summary>
  /// Removes the trailing assistant message, if any. For hosts whose persist of the
  /// assistant message failed: the transcript must not carry context the DB rejected.
  /// Safe no-op when the last message is not an assistant message.
  /// </summary>
  public void DiscardLastAssistantMessage()
  {
    if (_messages.Count > 0 && _messages[^1].Role == ChatRole.Assistant)
    {
      _messages.RemoveAt(_messages.Count - 1);
    }
  }

  /// <summary>Starts a fresh conversation: rebuilds the initial messages from the cached catalogs and clears the budget anchor.</summary>
  public void Reset()
  {
    _messages.Clear();
    _messages.AddRange(ToimiClientFactory.CreateInitialMessages(SkillSummary, TypeCatalog));
    _budget.Reset();
  }

  public ValueTask DisposeAsync()
  {
    return _aggregator.DisposeAsync();
  }

  private IEnumerable<TurnUpdate> DrainToolEvents(List<object> accumulated)
  {
    while (_notifier.TryDequeueEvent(out var evt))
    {
      switch (evt)
      {
        case ToolCallEvent tc:
          accumulated.Add(tc);
          yield return new ToolCallUpdate(tc.CallId, tc.Name, tc.Arguments);
          break;
        case ToolResultEvent tr:
          accumulated.Add(tr);
          yield return new ToolResultUpdate(tr.CallId, tr.Result, tr.DurationMs);
          break;
        default:
          break;
      }
    }
  }
}
