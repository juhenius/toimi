using Microsoft.EntityFrameworkCore;

namespace Toimi.Core.Data;

public class ConversationRepository(ToimiDbContext dbContext)
{
  public async Task<Conversation> CreateAsync(string kind = Conversation.ChatKind, Guid? parentConversationId = null, string? title = null)
  {
    var conversation = new Conversation { Kind = kind, ParentConversationId = parentConversationId, Title = title };
    dbContext.Conversations.Add(conversation);
    await dbContext.SaveChangesAsync();
    return conversation;
  }

  public async Task<Conversation?> GetByIdAsync(Guid id)
  {
    return await dbContext.Conversations
      .Include(c => c.Messages.OrderBy(m => m.CreatedAt))
      .FirstOrDefaultAsync(c => c.Id == id);
  }

  public async Task<List<Conversation>> ListRecentAsync(int limit = 20)
  {
    // Subtask transcripts are debugging/accounting records, not chats — keep them
    // out of the conversation sidebar.
    return await dbContext.Conversations
      .Where(c => c.Kind == Conversation.ChatKind)
      .OrderByDescending(c => c.LastMessageAt)
      .Take(limit)
      .ToListAsync();
  }

  public async Task<ConversationMessage> AddMessageAsync(
    Guid conversationId, string role, string content,
    string? toolCallsJson = null,
    int? promptTokens = null, int? completionTokens = null, int? totalTokens = null,
    string? model = null)
  {
    var message = new ConversationMessage
    {
      ConversationId = conversationId,
      Role = role,
      Content = content,
      ToolCallsJson = toolCallsJson,
      PromptTokens = promptTokens,
      CompletionTokens = completionTokens,
      TotalTokens = totalTokens,
      Model = model,
    };

    dbContext.ConversationMessages.Add(message);

    var conversation = await dbContext.Conversations.FindAsync(conversationId);
    conversation?.LastMessageAt = DateTimeOffset.UtcNow;

    await dbContext.SaveChangesAsync();
    return message;
  }

  public async Task UpdateTitleAsync(Guid id, string title)
  {
    var conversation = await dbContext.Conversations.FindAsync(id);
    if (conversation is not null)
    {
      conversation.Title = title;
      await dbContext.SaveChangesAsync();
    }
  }

  public async Task<bool> DeleteAsync(Guid id)
  {
    var conversation = await dbContext.Conversations.FindAsync(id);
    if (conversation is null)
    {
      return false;
    }

    dbContext.Conversations.Remove(conversation);
    await dbContext.SaveChangesAsync();
    return true;
  }
}
