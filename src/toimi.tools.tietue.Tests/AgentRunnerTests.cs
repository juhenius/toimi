using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Toimi.Core;
using Toimi.Core.Configuration;
using Toimi.Core.Llm;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class AgentRunnerTests
{
  private sealed class StreamingFakeChatClient : IChatClient
  {
    public List<ChatResponseUpdate> Updates { get; set; } = [new(ChatRole.Assistant, "agent says hi")];
    public List<List<ChatMessage>> Requests { get; } = [];
    public bool Hang { get; set; }
    public bool ThrowBoom { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      if (ThrowBoom)
      {
        throw new InvalidOperationException("boom");
      }

      if (Hang)
      {
        // Completes only by cancellation — deterministically exercises the
        // timeout and caller-cancellation branches.
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
      }

      Requests.Add([.. messages]);
      foreach (var update in Updates)
      {
        yield return update;
      }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
      return null;
    }

    public void Dispose()
    {
    }
  }

  private sealed class FakeLlmProvider(IChatClient chat) : ILlmClientProvider
  {
    public string ResolveModel(ModelTier tier)
    {
      return "fake-model";
    }

    public LlmSession Create(ModelTier tier = ModelTier.Fast)
    {
      var notifier = new ToolCallNotifier(chat);
      return new LlmSession(notifier, notifier, ResolveModel(tier));
    }
  }

  private static Entity SomeEntity()
  {
    return new Entity
    {
      Id = Guid.NewGuid(),
      Type = "schedule",
      Data = JsonDocument.Parse(/*lang=json,strict*/ """{"name":"daily"}"""),
      Tags = [],
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
  }

  // Empty McpServers: fully offline, the aggregator connects to nothing.
  private static ToimiConfiguration Config(int timeoutSeconds = 300)
  {
    return new ToimiConfiguration
    {
      OpenAI = new OpenAIOptions { ApiKey = "test" },
      AgentRunTimeoutSeconds = timeoutSeconds,
    };
  }

  [Fact]
  public async Task Successful_run_returns_response_and_real_usage()
  {
    var chat = new StreamingFakeChatClient
    {
      Updates =
      [
        new(ChatRole.Assistant, "agent says hi"),
        new(ChatRole.Assistant, [new UsageContent(new UsageDetails { InputTokenCount = 1200, OutputTokenCount = 340, TotalTokenCount = 1540 })]),
      ],
    };
    var runner = new AgentRunner(Config(), new FakeLlmProvider(chat));

    var result = await runner.RunAsync(SomeEntity(), "do the thing");

    Assert.True(result.Success);
    Assert.Equal("agent says hi", result.Response);
    Assert.Null(result.Error);
    Assert.Equal(1200, result.PromptTokens);
    Assert.Equal(340, result.CompletionTokens);
  }

  [Fact]
  public async Task Tool_calls_serialize_in_the_unified_client_wire_shape()
  {
    var chat = new StreamingFakeChatClient
    {
      Updates =
      [
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
        new(ChatRole.Assistant, "found it"),
      ],
    };
    var runner = new AgentRunner(Config(), new FakeLlmProvider(chat));

    var result = await runner.RunAsync(SomeEntity(), "find milk");

    // The dialect the React client's replay parser reads — previously tietue
    // serialized raw ToolCallEvent records with no "type" discriminator.
    Assert.NotNull(result.ToolCallsJson);
    Assert.Contains("\"type\":\"call\"", result.ToolCallsJson);
    Assert.Contains("\"CallId\":\"c1\"", result.ToolCallsJson);
    Assert.Contains("\"Name\":\"search\"", result.ToolCallsJson);
  }

  [Fact]
  public async Task Entity_context_rides_along_as_a_fenced_system_message()
  {
    var chat = new StreamingFakeChatClient();
    var runner = new AgentRunner(Config(), new FakeLlmProvider(chat));
    var entity = SomeEntity();

    await runner.RunAsync(entity, "act");

    var request = Assert.Single(chat.Requests);
    Assert.Contains(request, m => m.Role == ChatRole.System && (m.Text ?? "").Contains($"<entity_data id=\"{entity.Id}\""));
    Assert.Equal(ChatRole.User, request[^1].Role);
    Assert.Equal("act", request[^1].Text);
  }

  [Fact]
  public async Task Timeout_returns_an_error_result_instead_of_throwing()
  {
    var chat = new StreamingFakeChatClient { Hang = true };
    var runner = new AgentRunner(Config(timeoutSeconds: 0), new FakeLlmProvider(chat));

    var result = await runner.RunAsync(SomeEntity(), "hang");

    Assert.False(result.Success);
    Assert.Contains("timed out", result.Error);
  }

  [Fact]
  public async Task Caller_cancellation_propagates_so_the_occurrence_is_retried()
  {
    var chat = new StreamingFakeChatClient { Hang = true };
    var runner = new AgentRunner(Config(), new FakeLlmProvider(chat));
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(SomeEntity(), "x", ct: cts.Token));
  }

  [Fact]
  public async Task Provider_failure_returns_an_error_result()
  {
    var chat = new StreamingFakeChatClient { ThrowBoom = true };
    var runner = new AgentRunner(Config(), new FakeLlmProvider(chat));

    var result = await runner.RunAsync(SomeEntity(), "x");

    Assert.False(result.Success);
    Assert.Equal("boom", result.Error);
  }
}
