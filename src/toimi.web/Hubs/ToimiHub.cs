using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Toimi.Core;
using Toimi.Core.Configuration;
using Toimi.Core.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.AI;

namespace Toimi.Web.Hubs;

public class ToimiHub(ToimiConfiguration config, ConversationRepository repository) : Hub
{
  private static readonly ConcurrentDictionary<string, ToimiSession> Sessions = new();
  private readonly ToimiConfiguration _config = config;
  private readonly ConversationRepository _repository = repository;

  public override async Task OnConnectedAsync()
  {
    try
    {
      var aggregator = new McpToolAggregator();
      await aggregator.ConnectAllAsync(_config.McpServers);

      var tools = aggregator.GetAllTools();
      var skillSummary = await aggregator.CallToolAsync("list_skills");
      var (toimiClient, notifier) = ToimiClientFactory.Create(_config);
      var toimiOptions = ToimiClientFactory.CreateRequestOptions(tools);
      var messages = ToimiClientFactory.CreateInitialMessages(skillSummary);

      // Check for conversationId query parameter
      var conversationIdParam = Context.GetHttpContext()?.Request.Query["conversationId"].ToString();
      Guid conversationId;

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
          await Clients.Caller.SendAsync("ConversationLoaded", conversationId, messagesJson);
        }
        else
        {
          // Invalid conversation ID, create new
          var newConversation = await _repository.CreateAsync();
          conversationId = newConversation.Id;
        }
      }
      else
      {
        var newConversation = await _repository.CreateAsync();
        conversationId = newConversation.Id;
      }

      Sessions[Context.ConnectionId] = new ToimiSession(
        aggregator, toimiClient, notifier, toimiOptions, messages, skillSummary, conversationId);

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

    session.Messages.Add(new(ChatRole.User, message));

    // Save user message to DB
    await _repository.AddMessageAsync(session.ConversationId, "user", message);

    try
    {
      var fullResponse = new StringBuilder();
      var toolCallEvents = new List<object>();

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
        }
      }

      // Drain any remaining events after streaming completes
      await DrainToolEvents(session.Notifier, toolCallEvents);

      var responseText = fullResponse.ToString();
      session.Messages.Add(new(ChatRole.Assistant, responseText));

      // Serialize tool calls JSON
      string? toolCallsJson = toolCallEvents.Count > 0
        ? JsonSerializer.Serialize(toolCallEvents)
        : null;

      // Save assistant message to DB
      await _repository.AddMessageAsync(session.ConversationId, "assistant", responseText, toolCallsJson);

      // Auto-title: set title on first exchange
      if (session.Messages.Count(m => m.Role == ChatRole.User) == 1)
      {
        var title = message.Length > 50 ? message[..50] : message;
        await _repository.UpdateTitleAsync(session.ConversationId, title);
      }

      await Clients.Caller.SendAsync("MessageComplete", responseText);
    }
    catch (Exception ex)
    {
      session.Messages.RemoveAt(session.Messages.Count - 1);
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

    var newConversation = await _repository.CreateAsync();
    var messages = ToimiClientFactory.CreateInitialMessages(session.SkillSummary);

    // Replace the session with a new conversation
    Sessions[Context.ConnectionId] = session with
    {
      Messages = messages,
      ConversationId = newConversation.Id,
    };

    await Clients.Caller.SendAsync("ConversationLoaded", newConversation.Id, "[]");
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
    Guid ConversationId);
}
