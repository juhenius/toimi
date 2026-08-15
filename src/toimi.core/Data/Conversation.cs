namespace Toimi.Core.Data;

public class Conversation
{
  public const string ChatKind = "chat";
  public const string SubtaskKind = "subtask";

  public Guid Id { get; set; }
  public string? Title { get; set; }

  /// <summary>"chat" for user conversations, "subtask" for delegated subtask transcripts.</summary>
  public string Kind { get; set; } = ChatKind;

  /// <summary>For subtasks: the conversation (chat or subtask) whose turn delegated this one. Null when the delegator has no persisted conversation.</summary>
  public Guid? ParentConversationId { get; set; }

  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset LastMessageAt { get; set; }
  public ICollection<ConversationMessage> Messages { get; set; } = [];
}
