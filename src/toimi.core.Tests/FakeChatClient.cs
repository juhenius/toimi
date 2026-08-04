using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Toimi.Core.Tests;

public sealed class FakeChatClient : IChatClient
{
  public List<List<ChatMessage>> Requests { get; } = [];
  public string NextResponseText { get; set; } = "summary text";
  public ChatMessage? NextResponseMessage { get; set; }
  public List<ChatResponseUpdate> StreamUpdates { get; set; } = [];
  public bool Throw { get; set; }
  public int? ThrowAfterStreamUpdates { get; set; }

  public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
  {
    Requests.Add([.. messages]);
    return Throw
      ? throw new InvalidOperationException("simulated summarization failure")
      : Task.FromResult(new ChatResponse(NextResponseMessage ?? new ChatMessage(ChatRole.Assistant, NextResponseText)));
  }

  public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    Requests.Add([.. messages]);
    var emitted = 0;
    foreach (var update in StreamUpdates)
    {
      yield return update;
      emitted++;
      if (ThrowAfterStreamUpdates is { } n && emitted >= n)
      {
        throw new InvalidOperationException("simulated stream failure");
      }
    }

    await Task.CompletedTask;
  }

  public object? GetService(Type serviceType, object? serviceKey = null)
  {
    return null;
  }

  public void Dispose()
  {
  }
}
