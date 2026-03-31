namespace Toimi.Core.Data;

public class Conversation
{
  public Guid Id { get; set; }
  public string? Title { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset LastMessageAt { get; set; }
  public ICollection<ConversationMessage> Messages { get; set; } = [];
}
