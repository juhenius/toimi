using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ToolCallNotifierTests
{
  private static ChatResponseUpdate CallUpdate(string callId, string name, IDictionary<string, object?>? args = null)
  {
    return new ChatResponseUpdate(ChatRole.Assistant, [new FunctionCallContent(callId, name, args)]);
  }

  private static async Task DrainStreamAsync(ToolCallNotifier notifier, IEnumerable<ChatMessage> messages)
  {
    await foreach (var _ in notifier.GetStreamingResponseAsync(messages))
    {
    }
  }

  private static List<object?> DequeueAll(ToolCallNotifier notifier)
  {
    var events = new List<object?>();
    while (notifier.TryDequeueEvent(out var evt))
    {
      events.Add(evt);
    }

    return events;
  }

  [Fact]
  public async Task Streaming_call_content_enqueues_event_with_serialized_args()
  {
    var fake = new FakeChatClient
    {
      StreamUpdates = [CallUpdate("c1", "search", new Dictionary<string, object?> { ["query"] = "milk" })],
    };
    var notifier = new ToolCallNotifier(fake);

    await DrainStreamAsync(notifier, []);

    var evt = Assert.IsType<ToolCallEvent>(Assert.Single(DequeueAll(notifier)));
    Assert.Equal("c1", evt.CallId);
    Assert.Equal("search", evt.Name);
    Assert.Contains("milk", evt.Arguments);
  }

  [Fact]
  public async Task Null_arguments_serialize_as_empty_object_not_the_string_null()
  {
    var fake = new FakeChatClient { StreamUpdates = [CallUpdate("c1", "ping", args: null)] };
    var notifier = new ToolCallNotifier(fake);

    await DrainStreamAsync(notifier, []);

    var evt = Assert.IsType<ToolCallEvent>(Assert.Single(DequeueAll(notifier)));
    Assert.Equal("{}", evt.Arguments);
  }

  [Fact]
  public async Task Result_in_next_request_enqueues_result_event_exactly_once()
  {
    var fake = new FakeChatClient { StreamUpdates = [CallUpdate("c1", "search")] };
    var notifier = new ToolCallNotifier(fake);
    await DrainStreamAsync(notifier, []);
    DequeueAll(notifier); // consume the call event

    var withResult = new List<ChatMessage> { new(ChatRole.Tool, [new FunctionResultContent("c1", "found 3")]) };
    fake.StreamUpdates = [];
    await DrainStreamAsync(notifier, withResult);

    var evt = Assert.IsType<ToolResultEvent>(Assert.Single(DequeueAll(notifier)));
    Assert.Equal("c1", evt.CallId);
    Assert.Equal("found 3", evt.Result);
    Assert.True(evt.DurationMs >= 0);

    // The timer is removed on first match: replaying the same result must NOT
    // produce a second event (this is what keeps reconnect replays from
    // double-completing tool cards).
    await DrainStreamAsync(notifier, withResult);
    Assert.Empty(DequeueAll(notifier));
  }

  [Fact]
  public async Task Orphan_result_with_unknown_call_id_is_dropped()
  {
    // Current contract: a result whose call was never observed by this notifier
    // instance produces no event. ToimiHub replays history through a FRESH
    // notifier on reconnect, so replayed results are silently dropped rather
    // than crashing — pin that this stays a drop, not a throw.
    var fake = new FakeChatClient();
    var notifier = new ToolCallNotifier(fake);

    await DrainStreamAsync(notifier, [new ChatMessage(ChatRole.Tool, [new FunctionResultContent("never-seen", "x")])]);

    Assert.Empty(DequeueAll(notifier));
  }

  [Fact]
  public async Task Events_dequeue_in_fifo_order()
  {
    var fake = new FakeChatClient { StreamUpdates = [CallUpdate("c1", "first"), CallUpdate("c2", "second")] };
    var notifier = new ToolCallNotifier(fake);

    await DrainStreamAsync(notifier, []);

    var events = DequeueAll(notifier);
    Assert.Equal(2, events.Count);
    Assert.Equal("c1", Assert.IsType<ToolCallEvent>(events[0]).CallId);
    Assert.Equal("c2", Assert.IsType<ToolCallEvent>(events[1]).CallId);
  }

  [Fact]
  public async Task Non_streaming_response_path_also_captures_calls()
  {
    var fake = new FakeChatClient
    {
      NextResponseMessage = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c9", "lookup", null)]),
    };
    var notifier = new ToolCallNotifier(fake);

    await notifier.GetResponseAsync([]);

    var evt = Assert.IsType<ToolCallEvent>(Assert.Single(DequeueAll(notifier)));
    Assert.Equal("c9", evt.CallId);
    Assert.Equal("lookup", evt.Name);
  }
}
