using Microsoft.Extensions.AI;
using Toimi.Core.Configuration;
using Toimi.Core.Llm;
using Xunit;

namespace Toimi.Core.Tests;

public class DelegationTests
{
  /// <summary>Tier-aware fake: hands out a separate FakeChatClient per tier and records what was asked for.</summary>
  private sealed class TieredFakeLlmProvider : ILlmClientProvider
  {
    public FakeChatClient FastChat { get; } = new();
    public FakeChatClient SmartChat { get; } = new();
    public List<ModelTier> CreatedTiers { get; } = [];
    public bool HasDistinctSmartModel { get; init; } = true;

    public string ResolveModel(ModelTier tier)
    {
      return tier == ModelTier.Smart && HasDistinctSmartModel ? "smart-model" : "fast-model";
    }

    public LlmSession Create(ModelTier tier = ModelTier.Fast)
    {
      CreatedTiers.Add(tier);
      var chat = tier == ModelTier.Smart && HasDistinctSmartModel ? SmartChat : FastChat;
      var notifier = new ToolCallNotifier(chat);
      return new LlmSession(notifier, notifier, ResolveModel(tier));
    }
  }

  private sealed class RecordingSubtaskStore : ISubtaskStore
  {
    public List<(Guid? ParentId, string Title)> Created { get; } = [];
    public List<(Guid Id, string Role, string Content, string? Model)> Messages { get; } = [];
    public Guid NextId { get; set; } = Guid.NewGuid();

    public Task<Guid> CreateAsync(Guid? parentConversationId, string title, CancellationToken ct = default)
    {
      Created.Add((parentConversationId, title));
      return Task.FromResult(NextId);
    }

    public Task AddMessageAsync(
      Guid subtaskConversationId, string role, string content, string? toolCallsJson = null,
      int? promptTokens = null, int? completionTokens = null, int? totalTokens = null,
      string? model = null, CancellationToken ct = default)
    {
      Messages.Add((subtaskConversationId, role, content, model));
      return Task.CompletedTask;
    }
  }

  // Empty McpServers: the subtask agent bootstraps fully offline.
  private static ToimiConfiguration Config()
  {
    return new ToimiConfiguration { OpenAI = new OpenAIOptions { ApiKey = "test" } };
  }

  private static async Task<string?> InvokeAsync(AIFunction tool, string task, string? model = null)
  {
    var arguments = new AIFunctionArguments { ["task"] = task };
    if (model is not null)
    {
      arguments["model"] = model;
    }

    var result = await tool.InvokeAsync(arguments);
    return result?.ToString();
  }

  [Fact]
  public void Tool_description_notes_when_no_distinct_smart_model_exists()
  {
    var without = Delegation.CreateTool(Config(), new TieredFakeLlmProvider { HasDistinctSmartModel = false }, new SubtaskOptions(), null);
    var with = Delegation.CreateTool(Config(), new TieredFakeLlmProvider(), new SubtaskOptions(), null);

    Assert.Equal("delegate", without.Name);
    Assert.Contains("no separate smart model is configured", without.Description);
    Assert.DoesNotContain("no separate smart model is configured", with.Description);
  }

  [Fact]
  public async Task Subtask_runs_on_the_requested_tier_and_returns_its_final_text()
  {
    var provider = new TieredFakeLlmProvider();
    provider.SmartChat.StreamUpdates = [new(ChatRole.Assistant, "smart answer")];
    var tool = Delegation.CreateTool(Config(), provider, new SubtaskOptions(), null);

    var result = await InvokeAsync(tool, "hard question", model: "smart");

    Assert.Equal("smart answer", result);
    Assert.Contains(ModelTier.Smart, provider.CreatedTiers);
  }

  [Fact]
  public async Task Subtask_defaults_to_the_fast_tier()
  {
    var provider = new TieredFakeLlmProvider();
    provider.FastChat.StreamUpdates = [new(ChatRole.Assistant, "fast answer")];
    var tool = Delegation.CreateTool(Config(), provider, new SubtaskOptions(), null);

    var result = await InvokeAsync(tool, "easy chore");

    Assert.Equal("fast answer", result);
    Assert.DoesNotContain(ModelTier.Smart, provider.CreatedTiers);
  }

  [Fact]
  public async Task Long_results_are_truncated_with_a_marker()
  {
    var provider = new TieredFakeLlmProvider();
    provider.FastChat.StreamUpdates = [new(ChatRole.Assistant, new string('x', Delegation.MaxResultChars + 500))];
    var tool = Delegation.CreateTool(Config(), provider, new SubtaskOptions(), null);

    var result = await InvokeAsync(tool, "fetch a huge page");

    Assert.NotNull(result);
    Assert.Contains("[subtask result truncated", result);
    Assert.True(result.Length < Delegation.MaxResultChars + 200);
  }

  [Fact]
  public async Task Subtask_transcript_is_recorded_with_parent_link_and_model()
  {
    var provider = new TieredFakeLlmProvider();
    provider.SmartChat.StreamUpdates = [new(ChatRole.Assistant, "the answer")];
    var store = new RecordingSubtaskStore();
    var parentId = Guid.NewGuid();
    var tool = Delegation.CreateTool(Config(), provider, new SubtaskOptions(store, () => parentId), null);

    await InvokeAsync(tool, "analyze this", model: "smart");

    var (ParentId, Title) = Assert.Single(store.Created);
    Assert.Equal(parentId, ParentId);
    Assert.Equal("analyze this", Title);
    Assert.Equal(2, store.Messages.Count);
    Assert.Equal(("user", "analyze this", null), (store.Messages[0].Role, store.Messages[0].Content, store.Messages[0].Model));
    Assert.Equal(("assistant", "the answer", (string?)"smart-model"), (store.Messages[1].Role, store.Messages[1].Content, store.Messages[1].Model));
  }

  [Fact]
  public async Task Store_failures_do_not_fail_the_subtask()
  {
    var provider = new TieredFakeLlmProvider();
    provider.FastChat.StreamUpdates = [new(ChatRole.Assistant, "still works")];
    var store = new ThrowingSubtaskStore();
    var tool = Delegation.CreateTool(Config(), provider, new SubtaskOptions(store, () => null), null);

    var result = await InvokeAsync(tool, "task");

    Assert.Equal("still works", result);
  }

  private sealed class ThrowingSubtaskStore : ISubtaskStore
  {
    public Task<Guid> CreateAsync(Guid? parentConversationId, string title, CancellationToken ct = default)
    {
      throw new InvalidOperationException("db down");
    }

    public Task AddMessageAsync(
      Guid subtaskConversationId, string role, string content, string? toolCallsJson = null,
      int? promptTokens = null, int? completionTokens = null, int? totalTokens = null,
      string? model = null, CancellationToken ct = default)
    {
      throw new InvalidOperationException("db down");
    }
  }

  [Fact]
  public async Task Agent_offers_delegate_until_the_depth_cap()
  {
    var provider = new TieredFakeLlmProvider();
    provider.FastChat.StreamUpdates = [new(ChatRole.Assistant, "ok")];

    await using var depth0 = await ToimiAgent.StartAsync(Config(), provider, subtasks: new SubtaskOptions(Depth: 0));
    await depth0.RunTurnAsync("hi");
    Assert.Contains(provider.FastChat.LastOptions!.Tools!, t => t.Name == "delegate");

    await using var depth1 = await ToimiAgent.StartAsync(Config(), provider, subtasks: new SubtaskOptions(Depth: 1));
    await depth1.RunTurnAsync("hi");
    Assert.Contains(provider.FastChat.LastOptions!.Tools!, t => t.Name == "delegate");

    await using var depth2 = await ToimiAgent.StartAsync(Config(), provider, subtasks: new SubtaskOptions(Depth: 2));
    await depth2.RunTurnAsync("hi");
    Assert.DoesNotContain(provider.FastChat.LastOptions!.Tools ?? [], t => t.Name == "delegate");
  }

  [Fact]
  public async Task Turn_reports_the_model_that_served_it()
  {
    var provider = new TieredFakeLlmProvider();
    provider.SmartChat.StreamUpdates = [new(ChatRole.Assistant, "ok")];

    await using var agent = await ToimiAgent.StartAsync(Config(), provider, ModelTier.Smart);
    var turn = await agent.RunTurnAsync("hi");

    Assert.Equal("smart-model", turn.Model);
  }

  [Fact]
  public async Task Smart_turns_summarize_on_a_separate_fast_client()
  {
    var provider = new TieredFakeLlmProvider();
    provider.SmartChat.StreamUpdates = [new(ChatRole.Assistant, "ok")];

    await using var _ = await ToimiAgent.StartAsync(Config(), provider, ModelTier.Smart);

    Assert.Equal([ModelTier.Smart, ModelTier.Fast], provider.CreatedTiers);
  }
}
