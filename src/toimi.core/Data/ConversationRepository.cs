using Microsoft.EntityFrameworkCore;

namespace Toimi.Core.Data;

public class ConversationRepository(ToimiDbContext dbContext)
{
  public async Task<Conversation> CreateAsync()
  {
    var conversation = new Conversation();
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
    return await dbContext.Conversations
      .OrderByDescending(c => c.LastMessageAt)
      .Take(limit)
      .ToListAsync();
  }

  public async Task<ConversationMessage> AddMessageAsync(
    Guid conversationId, string role, string content, string? toolCallsJson = null)
  {
    var message = new ConversationMessage
    {
      ConversationId = conversationId,
      Role = role,
      Content = content,
      ToolCallsJson = toolCallsJson,
    };

    dbContext.ConversationMessages.Add(message);

    var conversation = await dbContext.Conversations.FindAsync(conversationId);
    if (conversation is not null)
      conversation.LastMessageAt = DateTimeOffset.UtcNow;

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
    if (conversation is null) return false;
    dbContext.Conversations.Remove(conversation);
    await dbContext.SaveChangesAsync();
    return true;
  }
}
