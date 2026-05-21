namespace Toimi.Core.Data;

public class ConversationMessage
{
  public Guid Id { get; set; }
  public Guid ConversationId { get; set; }
  public required string Role { get; set; }
  public required string Content { get; set; }
  public string? ToolCallsJson { get; set; }
  public int? PromptTokens { get; set; }
  public int? CompletionTokens { get; set; }
  public int? TotalTokens { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public Conversation Conversation { get; set; } = null!;
}
