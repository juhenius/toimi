using Toimi.Core;
using Toimi.Core.Data;

namespace Toimi.Web.Hubs;

/// <summary>
/// Persists delegated-subtask transcripts as conversations (kind "subtask",
/// linked to the delegating conversation). Resolves the scoped
/// <see cref="ConversationRepository"/> per call: subtasks run inside agent
/// sessions that outlive any single hub invocation's DI scope.
/// </summary>
public sealed class ScopedSubtaskStore(IServiceScopeFactory scopes) : ISubtaskStore
{
  public async Task<Guid> CreateAsync(Guid? parentConversationId, string title, CancellationToken ct = default)
  {
    using var scope = scopes.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<ConversationRepository>();
    var conversation = await repository.CreateAsync(Conversation.SubtaskKind, parentConversationId, title);
    return conversation.Id;
  }

  public async Task AddMessageAsync(
    Guid subtaskConversationId, string role, string content, string? toolCallsJson = null,
    int? promptTokens = null, int? completionTokens = null, int? totalTokens = null,
    string? model = null, CancellationToken ct = default)
  {
    using var scope = scopes.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<ConversationRepository>();
    await repository.AddMessageAsync(subtaskConversationId, role, content, toolCallsJson, promptTokens, completionTokens, totalTokens, model);
  }
}
