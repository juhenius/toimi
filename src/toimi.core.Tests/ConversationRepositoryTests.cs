// NOTE: EF Core InMemory ignores Postgres-only column defaults
// (`HasDefaultValueSql("now()")` / `HasDefaultValueSql("gen_random_uuid()")` in
// ConversationConfiguration/ConversationMessageConfiguration). Entities created
// here get CLR defaults (DateTimeOffset.MinValue, Guid.Empty is not applicable
// since Id is set by SaveChangesAsync's in-memory key generation, but CreatedAt
// stays unset) unless the test sets CreatedAt explicitly. Tests that depend on
// ordering by CreatedAt set it explicitly and re-save/re-fetch.
using Microsoft.EntityFrameworkCore;
using Toimi.Core.Data;
using Xunit;

namespace Toimi.Core.Tests;

public class ConversationRepositoryTests
{
  private static ToimiDbContext NewContext()
  {
    var options = new DbContextOptionsBuilder<ToimiDbContext>()
      .UseInMemoryDatabase($"core-{Guid.NewGuid()}")
      .Options;
    return new ToimiDbContext(options);
  }

  [Fact]
  public async Task Create_then_add_messages_round_trips_in_insertion_order()
  {
    await using var db = NewContext();
    var repository = new ConversationRepository(db);

    var conversation = await repository.CreateAsync();
    var userMessage = await repository.AddMessageAsync(
      conversation.Id, "user", "hello there");
    var assistantMessage = await repository.AddMessageAsync(
      conversation.Id, "assistant", "hi, how can I help?",
      toolCallsJson: /*lang=json,strict*/ """[{"name":"search"}]""",
      promptTokens: 10, completionTokens: 20, totalTokens: 30);

    // InMemory doesn't apply the Postgres now() default, so both messages
    // otherwise share the same (default) CreatedAt. Set explicit, distinct
    // timestamps so the repository's OrderBy(CreatedAt) include is meaningful.
    userMessage.CreatedAt = DateTimeOffset.UtcNow;
    assistantMessage.CreatedAt = userMessage.CreatedAt.AddSeconds(1);
    await db.SaveChangesAsync();

    var fetched = await repository.GetByIdAsync(conversation.Id);

    Assert.NotNull(fetched);
    Assert.Equal(2, fetched.Messages.Count);
    var ordered = fetched.Messages.OrderBy(m => m.CreatedAt).ToList();
    Assert.Equal("user", ordered[0].Role);
    Assert.Equal("hello there", ordered[0].Content);
    Assert.Equal("assistant", ordered[1].Role);
    Assert.Equal("hi, how can I help?", ordered[1].Content);
    Assert.Equal(/*lang=json,strict*/ """[{"name":"search"}]""", ordered[1].ToolCallsJson);
    Assert.Equal(10, ordered[1].PromptTokens);
    Assert.Equal(20, ordered[1].CompletionTokens);
    Assert.Equal(30, ordered[1].TotalTokens);
  }

  [Fact]
  public async Task AddMessage_bumps_LastMessageAt()
  {
    await using var db = NewContext();
    var repository = new ConversationRepository(db);

    var conversation = await repository.CreateAsync();
    conversation.CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    await db.SaveChangesAsync();

    await repository.AddMessageAsync(conversation.Id, "user", "hello");

    var fetched = await repository.GetByIdAsync(conversation.Id);
    Assert.NotNull(fetched);
    Assert.True(fetched.LastMessageAt > fetched.CreatedAt);
  }

  [Fact]
  public async Task ListRecent_returns_most_recently_active_first_and_respects_limit()
  {
    await using var db = NewContext();
    var repository = new ConversationRepository(db);

    var oldest = await repository.CreateAsync();
    var middle = await repository.CreateAsync();
    var newest = await repository.CreateAsync();

    oldest.LastMessageAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    middle.LastMessageAt = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
    newest.LastMessageAt = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
    await db.SaveChangesAsync();

    var recent = await repository.ListRecentAsync(limit: 2);

    Assert.Equal(2, recent.Count);
    Assert.Equal(newest.Id, recent[0].Id);
    Assert.Equal(middle.Id, recent[1].Id);
  }

  [Fact]
  public async Task Delete_removes_child_messages_and_returns_false_for_missing()
  {
    await using var db = NewContext();
    var repository = new ConversationRepository(db);

    var conversation = await repository.CreateAsync();
    await repository.AddMessageAsync(conversation.Id, "user", "hello");

    var deleted = await repository.DeleteAsync(conversation.Id);
    Assert.True(deleted);

    Assert.Empty(await db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).ToListAsync());
    Assert.Null(await repository.GetByIdAsync(conversation.Id));

    var missing = await repository.DeleteAsync(Guid.NewGuid());
    Assert.False(missing);
  }

  [Fact]
  public async Task AddMessage_to_unknown_conversation_pins_current_behavior()
  {
    // Characterization: InMemory has no FK enforcement, so AddMessageAsync
    // happily writes the orphan message row (Conversations.FindAsync returns
    // null, so the LastMessageAt bump is silently skipped via `conversation?.`).
    // Real Postgres would FK-throw on SaveChangesAsync instead. This pins the
    // repository's current (lack of) guard for a future hardening decision.
    await using var db = NewContext();
    var repository = new ConversationRepository(db);

    var unknownId = Guid.NewGuid();
    var message = await repository.AddMessageAsync(unknownId, "user", "orphan");

    Assert.Equal(unknownId, message.ConversationId);
    Assert.Contains(
      await db.ConversationMessages.ToListAsync(),
      m => m.Id == message.Id);
    Assert.Null(await repository.GetByIdAsync(unknownId));
  }

  [Fact]
  public async Task Subtask_conversations_carry_kind_parent_and_title_but_stay_out_of_the_recent_list()
  {
    await using var db = NewContext();
    var repository = new ConversationRepository(db);

    var chat = await repository.CreateAsync();
    var subtask = await repository.CreateAsync(Conversation.SubtaskKind, chat.Id, "fetch the page");

    Assert.Equal(Conversation.SubtaskKind, subtask.Kind);
    Assert.Equal(chat.Id, subtask.ParentConversationId);
    Assert.Equal("fetch the page", subtask.Title);

    var recent = await repository.ListRecentAsync();
    Assert.Contains(recent, c => c.Id == chat.Id);
    Assert.DoesNotContain(recent, c => c.Id == subtask.Id);
  }

  [Fact]
  public async Task Messages_persist_their_attributed_model()
  {
    await using var db = NewContext();
    var repository = new ConversationRepository(db);
    var conversation = await repository.CreateAsync();

    await repository.AddMessageAsync(conversation.Id, "assistant", "hi", model: "fast-m");

    var loaded = await repository.GetByIdAsync(conversation.Id);
    Assert.Equal("fast-m", Assert.Single(loaded!.Messages).Model);
  }
}
