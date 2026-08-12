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
  private readonly ConversationContext _context;
  private int _turnState; // 0 = idle, 1 = turn in progress (CAS-guarded)

  public int ToolCount { get; }
  public string? SkillSummary { get; }
  public string? TypeCatalog { get; }
  public IReadOnlyList<ChatMessage> Messages => _context.ToChatMessages();

  private ToimiAgent(
    ToimiConfiguration config, McpToolAggregator aggregator, LlmSession llm, ChatOptions options,
    ConversationContext context, string? skillSummary, string? typeCatalog, int toolCount)
  {
    _config = config;
    _aggregator = aggregator;
    _client = llm.Client;
    _notifier = llm.Notifier;
    _options = options;
    _context = context;
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
      var options = new ChatOptions { Tools = [.. tools] };
      var context = new ConversationContext(skillSummary, typeCatalog, budget ?? new ContextBudget());
      return new ToimiAgent(config, aggregator, llm, options, context, skillSummary, typeCatalog, tools.Count);
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
    _context.Append(role, text);
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
    // concurrently on the same agent — SingleUseTurn only guards re-enumeration
    // of one call's own sequence. CAS instead of a plain bool so two racing
    // enumerations cannot both slip past the check.
    if (Interlocked.CompareExchange(ref _turnState, 1, 0) != 0)
    {
      throw new InvalidOperationException("A turn is already in progress; SendAsync must be enumerated exactly once.");
    }

    try
    {
      _context.AppendUser(userText);
      _context.RefreshDynamicContext();

      // A summarization failure degrades gracefully inside CompactIfNeededAsync;
      // anything it does throw propagates to the host with the transcript
      // unchanged past the user message.
      await _context.CompactIfNeededAsync(_client, _config.MaxContextTokens, ct);

      var fullResponse = new StringBuilder();
      var toolEvents = new List<TurnUpdate>();
      UsageDetails? usage = null;

      await foreach (var update in _client.GetStreamingResponseAsync(_context.ToChatMessages(), _options, ct))
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

      // AppendAssistant anchors the budget to the prompt tokens of the transcript
      // AS SENT before appending the response — the ordering the old code
      // enforced by comment now lives inside ConversationContext.
      _context.AppendAssistant(responseText, (int?)usage?.InputTokenCount);

      // Prefer real usage from the final streaming update; fall back to the same
      // rough estimates the web host has always persisted.
      var promptTokens = (int?)usage?.InputTokenCount ?? (ContextBudget.TotalChars(_context.ToChatMessages()) / 4);
      var completionTokens = (int?)usage?.OutputTokenCount ?? (responseText.Length / 4);
      var totalTokens = (int?)usage?.TotalTokenCount ?? (promptTokens + completionTokens);

      yield return new TurnCompleted(responseText, ToolEventJson.Serialize(toolEvents), promptTokens, completionTokens, totalTokens);
    }
    finally
    {
      Volatile.Write(ref _turnState, 0);
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

    // SendAsync's contract: it either throws or terminates with TurnCompleted.
    return completed ?? throw new InvalidOperationException("turn ended without completing");
  }

  /// <summary>
  /// Removes the trailing assistant message, if any. For hosts whose persist of the
  /// assistant message failed: the transcript must not carry context the DB rejected.
  /// Safe no-op when the last message is not an assistant message.
  /// </summary>
  public void DiscardLastAssistantMessage()
  {
    _context.DiscardLastAssistantMessage();
  }

  /// <summary>Starts a fresh conversation: rebuilds the initial messages from the cached catalogs and clears the budget anchor.</summary>
  public void Reset()
  {
    _context.Reset();
  }

  public ValueTask DisposeAsync()
  {
    return _aggregator.DisposeAsync();
  }

  private IEnumerable<TurnUpdate> DrainToolEvents(List<TurnUpdate> accumulated)
  {
    while (_notifier.TryDequeueEvent(out var evt))
    {
      TurnUpdate? update = evt switch
      {
        ToolCallEvent tc => new ToolCallUpdate(tc.CallId, tc.Name, tc.Arguments),
        ToolResultEvent tr => new ToolResultUpdate(tr.CallId, tr.Result, tr.DurationMs),
        _ => null,
      };

      if (update is null)
      {
        continue;
      }

      accumulated.Add(update);
      yield return update;
    }
  }
}
