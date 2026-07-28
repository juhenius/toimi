using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Toimi.Core;
using Toimi.Core.Configuration;
using Toimi.Core.Data;
using Toimi.Core.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;

namespace Toimi.Web.Hubs;

public class ToimiHub(ToimiConfiguration config, ILlmClientProvider llmProvider, ConversationRepository repository, ILogger<ToimiHub> logger) : Hub
{
  private static readonly ConcurrentDictionary<string, ToimiSession> Sessions = new();
  private readonly ToimiConfiguration _config = config;
  private readonly ILlmClientProvider _llmProvider = llmProvider;
  private readonly ConversationRepository _repository = repository;

  public override async Task OnConnectedAsync()
  {
    try
    {
      var aggregator = new McpToolAggregator(logger);
      await aggregator.ConnectAllAsync(_config.McpServers);

      var tools = aggregator.GetAllTools();
      var skillSummary = await aggregator.CallToolAsync("list_skills");
      var typeCatalog = await aggregator.CallToolAsync("list_types");
      var (toimiClient, notifier) = _llmProvider.Create();
      var toimiOptions = ToimiClientFactory.CreateRequestOptions(tools);
      var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);

      // Check for conversationId query parameter
      var conversationIdParam = Context.GetHttpContext()?.Request.Query["conversationId"].ToString();

      // Lazy conversations: no DB row is written on connect. Only an existing,
      // query-param-named conversation resolves to an id here; a no-param connect
      // (or an unknown/deleted id) starts with a null ConversationId and no row.
      // The row is created on the first message (see SendMessage), which then emits
      // ConversationCreated so the client can learn its id for reconnect-resync.
      Guid? conversationId = null;

      if (!string.IsNullOrEmpty(conversationIdParam) && Guid.TryParse(conversationIdParam, out var existingId))
      {
        var conversation = await _repository.GetByIdAsync(existingId);
        if (conversation is not null)
        {
          conversationId = conversation.Id;

          // Replay stored messages into the ChatMessage list
          foreach (var msg in conversation.Messages)
          {
            var role = msg.Role == "user" ? ChatRole.User : ChatRole.Assistant;
            messages.Add(new(role, msg.Content));
          }

          // Send ConversationLoaded with messages
          var messagesJson = SerializeConversationMessages(conversation.Messages);
          await Clients.Caller.SendAsync("ConversationLoaded", conversation.Id, messagesJson);
        }
        // else: unknown/deleted id — fall through as a fresh, lazy conversation.
        // No ConversationLoaded is sent; the client keeps its empty view and learns
        // a real id from ConversationCreated once the first message creates the row.
      }
      // No-param connect is lazy too: no send, no row. The client's fresh view
      // (empty messages, no id) already reflects this state.

      Sessions[Context.ConnectionId] = new ToimiSession(
        aggregator, toimiClient, notifier, toimiOptions, messages, skillSummary, typeCatalog, conversationId);

      await Clients.Caller.SendAsync("Connected", tools.Count);
    }
    catch (Exception ex)
    {
      await Clients.Caller.SendAsync("Error", $"Failed to initialize: {ex.Message}");
      Context.Abort();
      return;
    }

    await base.OnConnectedAsync();
  }

  public override async Task OnDisconnectedAsync(Exception? exception)
  {
    if (Sessions.TryRemove(Context.ConnectionId, out var session))
    {
      await session.Aggregator.DisposeAsync();
    }

    await base.OnDisconnectedAsync(exception);
  }

  public async Task SendMessage(string message)
  {
    if (!Sessions.TryGetValue(Context.ConnectionId, out var session))
    {
      await Clients.Caller.SendAsync("Error", "Session not found. Please refresh.");
      return;
    }

    // Lazily create the conversation row on the first message, so no-param
    // connects / reconnects / abandoned "New" sessions never leave orphan rows.
    if (session.ConversationId is null)
    {
      var created = await _repository.CreateAsync();
      session = session with { ConversationId = created.Id };
      Sessions[Context.ConnectionId] = session;
      await Clients.Caller.SendAsync("ConversationCreated", created.Id);
    }

    session.Messages.Add(new(ChatRole.User, message));

    // Save user message to DB
    await _repository.AddMessageAsync(session.ConversationId.Value, "user", message);

    // Update current time
    ToimiClientFactory.RefreshDynamicContext(session.Messages);

    var assistantAppended = false;
    var assistantPersisted = false;
    try
    {
      // Compact context if needed. Inside the try so a summarization failure degrades
      // gracefully (CompactIfNeeded returns false) or is caught here rather than killing
      // the turn with the user message already persisted.
      await ContextManager.CompactIfNeeded(session.Messages, session.ChatClient, session.Budget, _config.MaxContextTokens, Context.ConnectionAborted);

      var fullResponse = new StringBuilder();
      var toolCallEvents = new List<object>();
      UsageDetails? usage = null;

      await foreach (var update in session.ChatClient.GetStreamingResponseAsync(
          session.Messages, session.ChatOptions, Context.ConnectionAborted))
      {
        // Drain tool call events from the notifier
        await DrainToolEvents(session.Notifier, toolCallEvents);

        foreach (var content in update.Contents)
        {
          if (content is TextContent textContent)
          {
            fullResponse.Append(textContent.Text);
            await Clients.Caller.SendAsync("ReceiveToken", textContent.Text);
          }

          if (content is UsageContent usageContent)
          {
            usage = usageContent.Details;
          }
        }
      }

      // Drain any remaining events after streaming completes
      await DrainToolEvents(session.Notifier, toolCallEvents);

      var responseText = fullResponse.ToString();

      // Anchor the budget to the real prompt-token count of the messages AS SENT.
      // The assistant response (appended below) then counts into the chars-delta,
      // keeping the estimate conservative rather than undercounting by one response.
      if (usage?.InputTokenCount is not null)
      {
        session.Budget.RecordUsage((int)usage.InputTokenCount.Value, session.Messages);
      }

      session.Messages.Add(new(ChatRole.Assistant, responseText));
      assistantAppended = true;

      // Serialize tool calls JSON
      var toolCallsJson = toolCallEvents.Count > 0
        ? JsonSerializer.Serialize(toolCallEvents)
        : null;

      // Prefer real usage from the final streaming update; fall back to a rough estimate.
      var promptTokens = (int?)usage?.InputTokenCount ?? (session.Messages.Sum(m => m.Text?.Length ?? 0) / 4);
      var completionTokens = (int?)usage?.OutputTokenCount ?? (responseText.Length / 4);
      var totalTokens = (int?)usage?.TotalTokenCount ?? (promptTokens + completionTokens);

      // Save assistant message to DB
      await _repository.AddMessageAsync(session.ConversationId.Value, "assistant", responseText, toolCallsJson,
        promptTokens: promptTokens,
        completionTokens: completionTokens,
        totalTokens: totalTokens);
      assistantPersisted = true;

      // Auto-title: set title on first exchange
      if (session.Messages.Count(m => m.Role == ChatRole.User) == 1)
      {
        var title = message.Length > 50 ? message[..50] : message;
        await _repository.UpdateTitleAsync(session.ConversationId.Value, title);
      }

      await Clients.Caller.SendAsync("MessageComplete", responseText);
    }
    catch (Exception ex)
    {
      // Only remove the assistant message if it was appended but NOT yet persisted.
      // A blind RemoveAt would strip the already-persisted user message (early throw),
      // and removing an assistant message the DB already has (throw after persist, e.g.
      // auto-title) would diverge in-memory context from the DB.
      if (assistantAppended && !assistantPersisted)
      {
        session.Messages.RemoveAt(session.Messages.Count - 1);
      }

      await Clients.Caller.SendAsync("Error", ex.Message);
    }
  }

  public async Task ListConversations()
  {
    var conversations = await _repository.ListRecentAsync();
    var json = JsonSerializer.Serialize(conversations.Select(c => new
    {
      id = c.Id,
      title = c.Title,
      createdAt = c.CreatedAt,
      lastMessageAt = c.LastMessageAt,
    }));
    await Clients.Caller.SendAsync("ConversationList", json);
  }

  public async Task NewConversation()
  {
    if (!Sessions.TryGetValue(Context.ConnectionId, out var session))
    {
      await Clients.Caller.SendAsync("Error", "Session not found. Please refresh.");
      return;
    }

    var messages = ToimiClientFactory.CreateInitialMessages(session.SkillSummary, session.TypeCatalog);

    // Start a fresh, lazy conversation: clear in-memory state but write no DB row.
    // The row is created on the first message (ConversationCreated then tells the
    // client its id), so an abandoned "New" never leaves an orphan row.
    Sessions[Context.ConnectionId] = session with
    {
      Messages = messages,
      ConversationId = null,
      Budget = new(),
    };

    // Distinct "new/empty" signal (not a ConversationLoaded with a real id): the
    // client resets its view and forgets any current id until the first message.
    await Clients.Caller.SendAsync("ConversationReset");
  }

  private async Task DrainToolEvents(ToolCallNotifier notifier, List<object>? toolCallEvents = null)
  {
    while (notifier.TryDequeueEvent(out var evt))
    {
      switch (evt)
      {
        case ToolCallEvent tc:
          toolCallEvents?.Add(new { type = "call", tc.CallId, tc.Name, tc.Arguments });
          await Clients.Caller.SendAsync("ToolCallStart", tc.CallId, tc.Name, tc.Arguments);
          break;
        case ToolResultEvent tr:
          toolCallEvents?.Add(new { type = "result", tr.CallId, tr.Result, tr.DurationMs });
          await Clients.Caller.SendAsync("ToolCallEnd", tr.CallId, tr.Result, tr.DurationMs);
          break;
        default:
          break;
      }
    }
  }

  private static string SerializeConversationMessages(ICollection<ConversationMessage> messages)
  {
    return JsonSerializer.Serialize(messages.Select(m => new
    {
      role = m.Role,
      content = m.Content,
      toolCallsJson = m.ToolCallsJson,
    }));
  }

  private sealed record ToimiSession(
    McpToolAggregator Aggregator,
    IChatClient ChatClient,
    ToolCallNotifier Notifier,
    ChatOptions ChatOptions,
    List<ChatMessage> Messages,
    string? SkillSummary,
    string? TypeCatalog,
    Guid? ConversationId)
  {
    public ContextBudget Budget { get; init; } = new();
  }
}
