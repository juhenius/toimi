using Microsoft.Extensions.AI;

namespace Toimi.Core.Tests;

public sealed class FakeChatClient : IChatClient
{
  public List<List<ChatMessage>> Requests { get; } = [];
  public string NextResponseText { get; set; } = "summary text";

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
  {
    Requests.Add([.. messages]);
    return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, NextResponseText)));
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
