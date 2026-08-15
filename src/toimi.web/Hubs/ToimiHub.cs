using System.Collections.Concurrent;
using System.Text.Json;
using Toimi.Core;
using Toimi.Core.Configuration;
using Toimi.Core.Data;
using Toimi.Core.Llm;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;

namespace Toimi.Web.Hubs;

public class ToimiHub(ToimiConfiguration config, ILlmClientProvider llmProvider, ConversationRepository repository, ISubtaskStore subtaskStore, ILogger<ToimiHub> logger) : Hub
{
  private static readonly ConcurrentDictionary<string, ToimiSession> Sessions = new();
  private readonly ToimiConfiguration _config = config;
  private readonly ILlmClientProvider _llmProvider = llmProvider;
  private readonly ConversationRepository _repository = repository;
  private readonly ISubtaskStore _subtaskStore = subtaskStore;

  public override async Task OnConnectedAsync()
  {
    ToimiAgent? agent = null;
    var registered = false;
    try
    {
      // The holder outlives session-record replacement: delegation resolves the
      // parent conversation id through it at delegation time, so subtasks link
      // correctly even though the conversation row is created lazily.
      var conversationIdHolder = new ConversationIdHolder();
      agent = await ToimiAgent.StartAsync(_config, _llmProvider,
        subtasks: new SubtaskOptions(_subtaskStore, () => conversationIdHolder.Id),
        logger: logger, ct: Context.ConnectionAborted);

      // Check for conversationId query parameter
      var conversationIdParam = Context.GetHttpContext()?.Request.Query["conversationId"].ToString();

      // Lazy conversations: no DB row is written on connect. Only an existing,
      // query-param-named conversation resolves to an id here; a no-param connect
      // (or an unknown/deleted id) starts with a null ConversationId and no row.
      // The row is created on the first message (see SendMessage), which then emits
      // ConversationCreated so the client can learn its id for reconnect-resync.
      if (!string.IsNullOrEmpty(conversationIdParam) && Guid.TryParse(conversationIdParam, out var existingId))
      {
        var conversation = await _repository.GetByIdAsync(existingId);
        if (conversation is not null)
        {
          conversationIdHolder.Id = conversation.Id;

          // Replay stored messages into the agent's transcript
          foreach (var msg in conversation.Messages)
          {
            agent.AppendMessage(msg.Role == "user" ? ChatRole.User : ChatRole.Assistant, msg.Content);
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

      Sessions[Context.ConnectionId] = new ToimiSession(agent, conversationIdHolder);
      registered = true;

      await Clients.Caller.SendAsync("Connected", agent.ToolCount);
    }
    catch (Exception ex)
    {
      // A started-but-unregistered agent would leak its MCP connections: without a
      // Sessions entry, OnDisconnectedAsync will never dispose it. Best-effort
      // dispose here; once registered, disconnect owns disposal.
      if (agent is not null && !registered)
      {
        try
        {
          await agent.DisposeAsync();
        }
        catch
        {
          // Disposal is best-effort on the failure path.
        }
      }

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
      await session.Agent.DisposeAsync();
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

    // The row is created exactly once, on the true first message, so ConversationId
    // being null here IS "this is the first message" — durable state, not the
    // in-memory window shape (compaction can never fake this).
    var isFirstMessage = session.ConversationId is null;

    try
    {
      // Lazily create the conversation row on the first message, so no-param
      // connects / reconnects / abandoned "New" sessions never leave orphan rows.
      if (session.ConversationId is null)
      {
        var created = await _repository.CreateAsync();
        session.ConversationIdHolder.Id = created.Id;
        await Clients.Caller.SendAsync("ConversationCreated", created.Id);
      }

      // Save user message to DB BEFORE handing it to the agent: on failure the
      // message exists nowhere (no rollback needed), and on success SendAsync's
      // contract keeps it in the transcript even if the turn later fails.
      await _repository.AddMessageAsync(session.ConversationId.Value, "user", message);
    }
    catch (Exception ex)
    {
      await Clients.Caller.SendAsync("Error", $"Failed to save your message: {ex.Message}");
      return;
    }

    try
    {
      TurnCompleted? completed = null;
      await foreach (var update in session.Agent.SendAsync(message, Context.ConnectionAborted))
      {
        switch (update)
        {
          case TokenUpdate token:
            await Clients.Caller.SendAsync("ReceiveToken", token.Text);
            break;
          case ToolCallUpdate call:
            await Clients.Caller.SendAsync("ToolCallStart", call.CallId, call.Name, call.Arguments);
            break;
          case ToolResultUpdate result:
            await Clients.Caller.SendAsync("ToolCallEnd", result.CallId, result.Result, result.DurationMs);
            break;
          case TurnCompleted done:
            completed = done;
            break;
          default:
            break;
        }
      }

      // SendAsync's contract: it either throws or terminates with TurnCompleted.
      var turn = completed ?? throw new InvalidOperationException("turn ended without completing");

      try
      {
        await _repository.AddMessageAsync(session.ConversationId.Value, "assistant", turn.ResponseText, turn.ToolCallsJson,
          promptTokens: turn.PromptTokens,
          completionTokens: turn.CompletionTokens,
          totalTokens: turn.TotalTokens,
          model: turn.Model);
      }
      catch
      {
        // The assistant message is in the agent's transcript but failed to persist:
        // strip it so in-memory context and DB stay in step. A failure AFTER this
        // persist (e.g. auto-title below) must NOT discard — the DB has the row.
        session.Agent.DiscardLastAssistantMessage();
        throw;
      }

      // Auto-title: set title on first exchange
      if (isFirstMessage)
      {
        var title = message.Length > 50 ? message[..50] : message;
        await _repository.UpdateTitleAsync(session.ConversationId.Value, title);
      }

      await Clients.Caller.SendAsync("MessageComplete", turn.ResponseText);
    }
    catch (Exception ex)
    {
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

    // Start a fresh, lazy conversation: reset the agent's transcript and budget but
    // write no DB row. The row is created on the first message (ConversationCreated
    // then tells the client its id), so an abandoned "New" never leaves an orphan row.
    session.Agent.Reset();
    session.ConversationIdHolder.Id = null;

    // Distinct "new/empty" signal (not a ConversationLoaded with a real id): the
    // client resets its view and forgets any current id until the first message.
    await Clients.Caller.SendAsync("ConversationReset");
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

  private sealed record ToimiSession(ToimiAgent Agent, ConversationIdHolder ConversationIdHolder)
  {
    /// <summary>Single source of truth is the holder — the same cell the delegation wiring reads — so the id cannot desynchronize.</summary>
    public Guid? ConversationId => ConversationIdHolder.Id;
  }

  /// <summary>Mutable cell holding the session's current conversation id (set lazily on first message, cleared on New).</summary>
  private sealed class ConversationIdHolder
  {
    public Guid? Id { get; set; }
  }
}
