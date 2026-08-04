using Microsoft.Extensions.AI;
using toimi.tools.tietue.Scripts;
using Toimi.Core;
using Toimi.Core.Llm;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class LlmExtractorTests
{
  private static (LlmExtractor extractor, FakeChatClient client) Make(string reply)
  {
    var client = new FakeChatClient(reply);
    return (new LlmExtractor(new FakeProvider(client)), client);
  }

  [Fact]
  public async Task Plain_json_passes_through()
  {
    var (extractor, _) = Make(/*lang=json,strict*/ """{"price":19.9}""");

    var result = await extractor.ExtractAsync("get price", "some html", null);

    Assert.Equal(/*lang=json,strict*/ """{"price":19.9}""", result);
  }

  [Fact]
  public async Task Json_code_fence_is_stripped()
  {
    var (extractor, _) = Make("```json\n{\"price\":19.9}\n```");

    var result = await extractor.ExtractAsync("get price", "some html", null);

    Assert.Equal(/*lang=json,strict*/ """{"price":19.9}""", result);
  }

  [Fact]
  public async Task Single_line_fence_is_stripped()
  {
    var (extractor, _) = Make("""```{"price":19.9}```""");

    var result = await extractor.ExtractAsync("get price", "some html", null);

    Assert.Equal(/*lang=json,strict*/ """{"price":19.9}""", result);
  }

  [Fact]
  public async Task Single_line_fence_with_language_tag_is_stripped()
  {
    var (extractor, _) = Make("""```json {"price":19.9}```""");

    var result = await extractor.ExtractAsync("get price", "some html", null);

    Assert.Equal(/*lang=json,strict*/ """{"price":19.9}""", result);
  }

  [Fact]
  public async Task Non_json_output_returns_null()
  {
    var (extractor, _) = Make("I could not find a price on this page.");

    Assert.Null(await extractor.ExtractAsync("get price", "some html", null));
  }

  [Fact]
  public async Task Null_schema_asks_for_any_json_value()
  {
    var (extractor, client) = Make(/*lang=json,strict*/ """{"ok":true}""");

    await extractor.ExtractAsync("get price", "some html", null);

    var user = Assert.Single(client.LastMessages!, m => m.Role == ChatRole.User);
    Assert.Contains("any JSON value", user.Text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Uses_deterministic_bounded_chat_options()
  {
    var (extractor, client) = Make(/*lang=json,strict*/ """{"ok":true}""");

    await extractor.ExtractAsync("get price", "some html", null);

    Assert.NotNull(client.LastOptions);
    Assert.Equal(0, client.LastOptions.Temperature);
    Assert.Equal(4096, client.LastOptions.MaxOutputTokens);
  }

  private sealed class FakeProvider(IChatClient client) : ILlmClientProvider
  {
    public LlmSession Create()
    {
      return new LlmSession(client, new ToolCallNotifier(client));
    }
  }

  private sealed class FakeChatClient(string reply) : IChatClient
  {
    public ChatOptions? LastOptions { get; private set; }
    public IReadOnlyList<ChatMessage>? LastMessages { get; private set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      LastMessages = [.. messages];
      LastOptions = options;
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      throw new NotSupportedException();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
      return null;
    }

    public void Dispose()
    {
    }
  }
}
