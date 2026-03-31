using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Toimi.Core;

public record ToolCallEvent(string CallId, string Name, string Arguments);
public record ToolResultEvent(string CallId, string Result, long DurationMs);

public class ToolCallNotifier(IChatClient inner) : DelegatingChatClient(inner)
{
  private readonly Dictionary<string, Stopwatch> _timers = new();
  private readonly ConcurrentQueue<object> _events = new();

  public bool TryDequeueEvent(out object? evt) => _events.TryDequeue(out evt);

  public override async Task<ChatResponse> GetResponseAsync(
      IEnumerable<ChatMessage> messages,
      ChatOptions? options = null,
      CancellationToken cancellationToken = default)
  {
    EnqueueResultsFromMessages(messages);
    var response = await base.GetResponseAsync(messages, options, cancellationToken);
    EnqueueCallsFromContents(response.Messages.SelectMany(m => m.Contents));
    return response;
  }

  public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
      IEnumerable<ChatMessage> messages,
      ChatOptions? options = null,
      [EnumeratorCancellation] CancellationToken cancellationToken = default)
  {
    EnqueueResultsFromMessages(messages);

    await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
    {
      EnqueueCallsFromContents(update.Contents);
      yield return update;
    }
  }

  private void EnqueueResultsFromMessages(IEnumerable<ChatMessage> messages)
  {
    foreach (var msg in messages)
    {
      foreach (var content in msg.Contents)
      {
        if (content is FunctionResultContent functionResult &&
            _timers.TryGetValue(functionResult.CallId, out var timer))
        {
          timer.Stop();
          var result = functionResult.Result?.ToString() ?? "";
          _events.Enqueue(new ToolResultEvent(functionResult.CallId, result, timer.ElapsedMilliseconds));
          _timers.Remove(functionResult.CallId);
        }
      }
    }
  }

  private void EnqueueCallsFromContents(IEnumerable<AIContent> contents)
  {
    foreach (var content in contents)
    {
      if (content is FunctionCallContent functionCall)
      {
        var args = functionCall.Arguments is not null
            ? JsonSerializer.Serialize(functionCall.Arguments)
            : "{}";
        _timers[functionCall.CallId] = Stopwatch.StartNew();
        _events.Enqueue(new ToolCallEvent(functionCall.CallId, functionCall.Name, args));
      }
    }
  }
}
