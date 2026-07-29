# Tier 2 Test Gaps Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pin the Tier-2 untested load-bearing behavior from the 2026-07-29 coverage review with tests, fixing the three concrete bugs found along the way (WebFetcher case-sensitive content-type, UpdateTrigger dead-trigger re-enable, koti ListEntities hard-fail on template API errors).

**Architecture:** Mostly characterization/pinning tests over existing behavior; production changes only where a test exposes a real defect. Branch base is `wip` (Tier 1 already landed there — ToimiHubTests, koti/notifications test projects, and the vitest harness all exist). TDD where a fix is made; plain pinning tests elsewhere.

**Tech Stack:** .NET 10 / xUnit 2.9.3, EF InMemory (already referenced where needed), repo-standard nested `StubHandler` HTTP fakes.

**Environment notes (critical):**
- Work from `/Users/jari/private/toimi/.claude/worktrees/tier2-test-gaps` (branch `worktree-tier2-test-gaps`). All paths relative to it.
- `dotnet` is NOT on PATH: `mise exec dotnet -- dotnet <args>`. npm: `mise exec node -- npm <args>`.
- Before each commit: `mise exec dotnet -- dotnet format <changed-csproj>` then `--verify-no-changes` for every modified C# project. IDE0005/IDE0022/IDE0046 + whitespace are errors.
- Commits: `<type>(<scope>): <subject>`, ending with the Co-Authored-By Claude line.
- **Characterization rule:** where a task says "pin actual behavior", run the test, observe, and encode the observed value with a comment. If observed behavior is a crash or otherwise surprising, report DONE_WITH_CONCERNS with the evidence instead of inventing a fix.

---

### Task 1: core — ToolCallNotifier unit tests (pure pinning)

`src/toimi.core/ToolCallNotifier.cs` has zero tests. Pin: streaming call capture + args serialization, result matching against started timers (one-shot), orphan-result drop, FIFO order, non-streaming path.

**Files:**
- Modify: `src/toimi.core.Tests/FakeChatClient.cs` (add scriptable streaming + response contents)
- Create: `src/toimi.core.Tests/ToolCallNotifierTests.cs`

- [ ] **Step 1: Extend FakeChatClient**

Replace the `GetStreamingResponseAsync` NotSupported stub and extend the class so the full file becomes:

```csharp
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
    foreach (var update in StreamUpdates)
    {
      yield return update;
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
```

Run the existing core suite to prove no regression: `mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj` (expect 41/41).

- [ ] **Step 2: Write the notifier tests**

Create `src/toimi.core.Tests/ToolCallNotifierTests.cs`:

```csharp
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
```

- [ ] **Step 3: Run and fix any observed-behavior mismatches**

Run: `mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter "FullyQualifiedName~ToolCallNotifierTests"`
These are pinning tests — expected to PASS against current behavior. If one fails, the observed behavior wins: adjust the assertion to pin reality and note it in your report (do NOT change ToolCallNotifier.cs).

- [ ] **Step 4: Full core suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
git add src/toimi.core.Tests/FakeChatClient.cs src/toimi.core.Tests/ToolCallNotifierTests.cs
git commit -m "test(core): pin ToolCallNotifier event capture contract"
```

---

### Task 2: core — RefreshDynamicContext round-trip tests (pure pinning)

`ToimiClientFactory.RefreshDynamicContext` (`src/toimi.core/ToimiClientFactory.cs:124-143`) has three silent early-returns; the load-bearing part is that only the first line changes and the skills/type catalog tail survives.

**Files:**
- Create: `src/toimi.core.Tests/ToimiClientFactoryTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using Microsoft.Extensions.AI;
using Xunit;

namespace Toimi.Core.Tests;

public class ToimiClientFactoryTests
{
  [Fact]
  public void Refresh_replaces_only_the_time_line_and_preserves_the_catalogs()
  {
    var messages = ToimiClientFactory.CreateInitialMessages("skillA — does things", "typeB — a schema");

    // Simulate a stale session: rewrite the first line to an obviously old timestamp,
    // exactly the shape RefreshDynamicContext must recognize.
    var text = messages[1].Text ?? "";
    var rest = text[text.IndexOf('\n')..];
    messages[1] = new ChatMessage(ChatRole.System, "Current time: 1999-01-01 00:00 UTC (stale)" + rest);

    ToimiClientFactory.RefreshDynamicContext(messages);

    var refreshed = messages[1].Text ?? "";
    Assert.DoesNotContain("1999", refreshed);
    Assert.StartsWith("Current time: ", refreshed);
    // The injected catalogs must survive the refresh — losing them mid-session
    // silently strips the model's knowledge of available skills and types.
    Assert.Contains("Available skills", refreshed);
    Assert.Contains("skillA", refreshed);
    Assert.Contains("Available data types", refreshed);
    Assert.Contains("typeB", refreshed);
  }

  [Fact]
  public void Refresh_is_a_silent_no_op_when_the_structure_does_not_match()
  {
    // Each of these shapes must neither throw nor mutate — but note the flip side
    // pinned here: if CreateInitialMessages ever changes its layout, Refresh
    // degrades to never updating the clock (silently), which is why this
    // round-trip suite exists.
    var single = new List<ChatMessage> { new(ChatRole.System, "only one") };
    ToimiClientFactory.RefreshDynamicContext(single);
    Assert.Equal("only one", single[0].Text);

    var wrongRole = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "hi") };
    ToimiClientFactory.RefreshDynamicContext(wrongRole);
    Assert.Equal("hi", wrongRole[1].Text);

    var wrongPrefix = new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.System, "not a time line\nrest") };
    ToimiClientFactory.RefreshDynamicContext(wrongPrefix);
    Assert.Equal("not a time line\nrest", wrongPrefix[1].Text);
  }

  [Fact]
  public void Initial_messages_omit_absent_catalog_sections()
  {
    var messages = ToimiClientFactory.CreateInitialMessages(skillSummary: null, typeCatalog: null);

    Assert.Equal(2, messages.Count);
    var context = messages[1].Text ?? "";
    Assert.StartsWith("Current time: ", context);
    Assert.DoesNotContain("Available skills", context);
    Assert.DoesNotContain("Available data types", context);
  }
}
```

- [ ] **Step 2: Run, verify pass (pinning), full suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
git add src/toimi.core.Tests/ToimiClientFactoryTests.cs
git commit -m "test(core): pin RefreshDynamicContext round-trip contract"
```

---

### Task 3: core — McpToolAggregator failure-mode tests (pure pinning)

The whole prompt-assembly degraded path depends on: unknown tool → null (not throw), dead servers swallowed, dispose safe when never connected.

**Files:**
- Create: `src/toimi.core.Tests/McpToolAggregatorTests.cs`

- [ ] **Step 1: Read `src/toimi.core/Configuration/ToimiOptions.cs`** to confirm `McpServerOptions` property names (Name, Transport, Command, ...) before writing.

- [ ] **Step 2: Write the tests**

```csharp
using Toimi.Core.Configuration;
using Xunit;

namespace Toimi.Core.Tests;

public class McpToolAggregatorTests
{
  [Fact]
  public async Task CallToolAsync_returns_null_for_unknown_tool_instead_of_throwing()
  {
    // ToimiHub/AgentRunner feed this straight into CreateInitialMessages: null
    // must mean "degrade gracefully", never an exception that aborts the session.
    var aggregator = new McpToolAggregator();

    Assert.Null(await aggregator.CallToolAsync("list_skills"));
  }

  [Fact]
  public async Task ConnectAllAsync_swallows_unreachable_servers_and_registers_no_tools()
  {
    // One dead MCP pod must not take down every session: each failed connect
    // logs a warning and is skipped.
    var aggregator = new McpToolAggregator();
    var servers = new List<McpServerOptions>
    {
      new() { Name = "bad1", Transport = McpTransportType.Stdio, Command = "/nonexistent-binary-toimi-test" },
      new() { Name = "bad2", Transport = McpTransportType.Stdio, Command = "/another-nonexistent-binary" },
    };

    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await aggregator.ConnectAllAsync(servers, cts.Token);

    Assert.Empty(aggregator.GetAllTools());
    await aggregator.DisposeAsync();
  }

  [Fact]
  public async Task DisposeAsync_on_a_never_connected_aggregator_is_a_no_op()
  {
    var aggregator = new McpToolAggregator();
    await aggregator.DisposeAsync();
  }
}
```

Adjust `McpServerOptions`/`McpTransportType` member usage to what Step 1 found (report any difference). If the unreachable-server test hangs beyond ~30s or is flaky, replace both stdio entries with `Transport = McpTransportType.Http, Url = "http://127.0.0.1:1/sse"` (closed port → fast connection refused) and report the substitution.

- [ ] **Step 3: Run, full suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes
git add src/toimi.core.Tests/McpToolAggregatorTests.cs
git commit -m "test(core): pin McpToolAggregator failure-mode contract"
```

---

### Task 4: web — hub tool-call persistence pin + mid-stream rollback tests + estimate consistency

Three related pieces on `ToimiHub`:
(a) Pin the PascalCase `toolCallsJson` persistence contract (the client's replay depends on it, `useToimi.ts` accepts both casings defensively — a serializer change today would break replay with no signal).
(b) Test the mid-stream error rollback states (the comment at the catch encodes them; no test guards them).
(c) Cosmetic consistency: the `promptTokens` fallback still uses text-only chars. `session.Messages` can never contain tool content today (only text is appended), so this is NOT observably testable — change it anyway for uniformity with Task 3 of Tier 1, and make `ContextBudget.TotalChars` public so toimi.web can call it. State in the commit that it's a consistency change.

**Files:**
- Modify: `src/toimi.core/ContextBudget.cs` (`internal static int TotalChars` → `public static int TotalChars`)
- Modify: `src/toimi.web/Hubs/ToimiHub.cs` (fallback line)
- Modify: `src/toimi.web.Tests/ToimiHubTests.cs` (scriptable fake + 2 new tests)

- [ ] **Step 1: Make the hub fake scriptable**

In `src/toimi.web.Tests/ToimiHubTests.cs`, replace `StreamingFakeChatClient` with:

```csharp
  private sealed class StreamingFakeChatClient : IChatClient
  {
    public List<ChatResponseUpdate> Updates { get; set; } = [new(ChatRole.Assistant, "hello from fake")];
    public int? ThrowAfterEmit { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
      return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summary")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
      var emitted = 0;
      foreach (var update in Updates)
      {
        yield return update;
        emitted++;
        if (ThrowAfterEmit is { } n && emitted >= n)
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
```

Change `FakeLlmProvider` to expose the fake and update `ConnectedHub` to return it:

```csharp
  private sealed class FakeLlmProvider : ILlmClientProvider
  {
    public StreamingFakeChatClient ChatClient { get; } = new();

    public (IChatClient Client, ToolCallNotifier Notifier) Create()
    {
      var notifier = new ToolCallNotifier(ChatClient);
      return (notifier, notifier);
    }
  }
```

`ConnectedHub` becomes `private static async Task<(ToimiHub Hub, FakeHubCallerClients Clients, ThrowingDbContext Db, StreamingFakeChatClient Chat)> ConnectedHub()` — construct `var llm = new FakeLlmProvider();`, pass it to the hub, return `llm.ChatClient` as the fourth element. Update the two existing tests' destructuring to `var (hub, clients, db, _) = ...`.

Run the existing hub tests to prove no regression before continuing: `mise exec dotnet -- dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj --filter "FullyQualifiedName~ToimiHubTests"` (expect 2/2).

- [ ] **Step 2: Add the two new tests**

```csharp
  [Fact]
  public async Task Tool_call_events_reach_the_client_and_persist_with_pascal_case_keys()
  {
    var (hub, clients, db, chat) = await ConnectedHub();
    chat.Updates =
    [
      new(ChatRole.Assistant, [new FunctionCallContent("c1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
      new(ChatRole.Assistant, "found it"),
    ];

    await hub.SendMessage("find milk");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "ToolCallStart" && (string?)s.Args[0] == "c1");

    // The persisted shape is the client's replay contract (useToimi.ts reads
    // CallId/Name/Arguments in PascalCase). A serializer-options change would
    // break conversation replay with no other signal — pin it.
    var assistant = db.ConversationMessages.Single(m => m.Role == "assistant");
    Assert.NotNull(assistant.ToolCallsJson);
    Assert.Contains("\"type\":\"call\"", assistant.ToolCallsJson);
    Assert.Contains("\"CallId\":\"c1\"", assistant.ToolCallsJson);
    Assert.Contains("search", assistant.ToolCallsJson);

    await hub.OnDisconnectedAsync(null);
  }

  [Fact]
  public async Task Mid_stream_failure_keeps_the_user_message_and_persists_no_assistant_row()
  {
    var (hub, clients, db, chat) = await ConnectedHub();
    chat.Updates = [new(ChatRole.Assistant, "partial ")];
    chat.ThrowAfterEmit = 1;

    await hub.SendMessage("doomed turn");

    // The user message persisted BEFORE the stream started; a mid-stream failure
    // must send Error, keep that row, and persist no assistant row.
    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "Error");
    Assert.DoesNotContain(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    var rows = db.ConversationMessages.ToList();
    var failedUser = Assert.Single(rows);
    Assert.Equal("user", failedUser.Role);
    Assert.Equal("doomed turn", failedUser.Content);

    // Recovery: the in-memory session must not carry a phantom assistant message.
    chat.ThrowAfterEmit = null;
    chat.Updates = [new(ChatRole.Assistant, "second answer")];
    await hub.SendMessage("second turn");

    Assert.Contains(clients.CallerProxy.Sent, s => s.Method == "MessageComplete");
    rows = db.ConversationMessages.ToList();
    Assert.Equal(3, rows.Count); // failed user + second turn's user/assistant pair
    Assert.Contains(rows, m => m.Role == "assistant" && m.Content == "second answer");

    await hub.OnDisconnectedAsync(null);
  }
```

These are pinning tests over existing behavior — expected to PASS. If either fails, investigate whether it exposes a real hub bug and report DONE_WITH_CONCERNS with the evidence; do not silently adjust the hub.

- [ ] **Step 3: The consistency change**

In `src/toimi.core/ContextBudget.cs`: `internal static int TotalChars(...)` → `public static int TotalChars(...)`.
In `src/toimi.web/Hubs/ToimiHub.cs`, the fallback line becomes:

```csharp
      var promptTokens = (int?)usage?.InputTokenCount ?? (ContextBudget.TotalChars(session.Messages) / 4);
```

- [ ] **Step 4: Full web + core suites, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj
mise exec dotnet -- dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.core/toimi.core.csproj && mise exec dotnet -- dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.web/toimi.web.csproj && mise exec dotnet -- dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj && mise exec dotnet -- dotnet format src/toimi.web.Tests/toimi.web.Tests.csproj --verify-no-changes
git add src/toimi.core/ContextBudget.cs src/toimi.web/Hubs/ToimiHub.cs src/toimi.web.Tests/ToimiHubTests.cs
git commit -m "test(web): pin tool-call persistence and mid-stream rollback; unify token fallback"
```

---

### Task 5: tietue — RecurrenceCalculator DST and bound edges (characterization)

`RecurrenceCalculatorTests.cs` covers one DST case. Pin the dangerous edges. These are characterization tests: run each, observe, encode observed values with comments. **If any case throws, STOP and report DONE_WITH_CONCERNS with the exception** — a throw here propagates uncaught through `Schedules.InitialNextFireAt` into MCP tools, and fixing that is a design decision for the controller, not this task.

**Files:**
- Modify: `src/toimi.tools.tietue.Tests/RecurrenceCalculatorTests.cs` (append tests)

- [ ] **Step 1: Append the tests**

```csharp
  [Fact]
  public void Nonexistent_wall_clock_in_spring_forward_gap_does_not_throw_or_vanish()
  {
    // 2026-03-29 Helsinki jumps 03:00 -> 04:00; a daily 03:30 rule has no valid
    // wall-clock that day. Whatever Ical.Net does (skip/shift), the schedule must
    // survive: the occurrence after the gap day must land back on 03:30 local.
    var start = new DateTimeOffset(2026, 3, 27, 3, 30, 0, TimeSpan.FromHours(2));

    var gapDay = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 3, 28, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.NotNull(gapDay);

    var afterGap = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", gapDay!.Value, "Europe/Helsinki");
    // 03:30 EEST (UTC+3) on 2026-03-30 == 00:30Z.
    Assert.Equal(new DateTimeOffset(2026, 3, 30, 0, 30, 0, TimeSpan.Zero), afterGap!.Value.ToUniversalTime());
  }

  [Fact]
  public void Ambiguous_wall_clock_in_fall_back_hour_fires_exactly_once()
  {
    // 2026-10-25 Helsinki repeats 03:00-04:00; a daily 03:30 rule has two candidate
    // instants that day. Consecutive occurrences must stay ~a day apart — a
    // double-fire inside the repeated hour would send duplicate notifications.
    var start = new DateTimeOffset(2026, 10, 23, 3, 30, 0, TimeSpan.FromHours(3));

    var first = RecurrenceCalculator.NextOccurrenceAfter(
      start, "FREQ=DAILY", new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero), "Europe/Helsinki");
    Assert.NotNull(first);
    var second = RecurrenceCalculator.NextOccurrenceAfter(start, "FREQ=DAILY", first!.Value, "Europe/Helsinki");
    Assert.NotNull(second);

    Assert.True(second!.Value - first.Value >= TimeSpan.FromHours(20),
      $"occurrences {first:o} and {second:o} are suspiciously close — double fire in the repeated hour");
  }

  [Fact]
  public void Count_bounded_rule_across_dst_yields_exactly_count_occurrences_at_stable_wall_clock()
  {
    // Daily 09:00 Helsinki, 5 occurrences spanning the 2026-03-29 spring-forward.
    var start = new DateTimeOffset(2026, 3, 27, 9, 0, 0, TimeSpan.FromHours(2));
    var occurrences = new List<DateTimeOffset>();
    var current = RecurrenceCalculator.NextOccurrenceOnOrAfter(
      start, "FREQ=DAILY;COUNT=5", start, "Europe/Helsinki");
    while (current is not null)
    {
      occurrences.Add(current.Value);
      current = RecurrenceCalculator.NextOccurrenceAfter(start, "FREQ=DAILY;COUNT=5", current.Value, "Europe/Helsinki");
    }

    Assert.Equal(5, occurrences.Count);
    // 09:00 EET (UTC+2) -> 07:00Z before the transition; 09:00 EEST (UTC+3) -> 06:00Z after.
    Assert.Equal(new DateTimeOffset(2026, 3, 27, 7, 0, 0, TimeSpan.Zero), occurrences[0].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 28, 7, 0, 0, TimeSpan.Zero), occurrences[1].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.Zero), occurrences[2].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 30, 6, 0, 0, TimeSpan.Zero), occurrences[3].ToUniversalTime());
    Assert.Equal(new DateTimeOffset(2026, 3, 31, 6, 0, 0, TimeSpan.Zero), occurrences[4].ToUniversalTime());
  }

  [Fact]
  public void Rules_sparser_than_the_two_year_window_return_null()
  {
    // Documented limitation (RecurrenceCalculator.Window): the next occurrence of
    // FREQ=YEARLY;INTERVAL=3 is beyond the search window, so scheduling returns
    // null — and SchedulerTick then DISABLES the trigger. Pinned so a future
    // window change is a conscious decision.
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=YEARLY;INTERVAL=3", Start);
    Assert.Null(next);
  }

  [Fact]
  public void Unknown_timezone_falls_back_to_utc_expansion_without_throwing()
  {
    var withBogusTz = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY", Start, "Mars/Olympus");
    var pureUtc = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY", Start);
    Assert.Equal(pureUtc, withBogusTz);
  }
```

- [ ] **Step 2: Run and characterize**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~RecurrenceCalculatorTests"`
For the gap-day test, the *observed* gap-day instant is unknown in advance (Ical.Net may shift 03:30→04:30 or skip). If the `afterGap`/count assertions fail with an off-by-one-hour or skipped-day pattern, pin the OBSERVED values, add a comment stating what Ical.Net actually does, and note it in your report. If anything THROWS, stop — DONE_WITH_CONCERNS with the stack trace.

- [ ] **Step 3: Full tietue suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue.Tests/RecurrenceCalculatorTests.cs
git commit -m "test(tietue): characterize recurrence DST and window edges"
```

---

### Task 6: tietue — UpdateTrigger dead-trigger re-enable (TDD fix)

**Bug:** `TriggerRepository.UpdateAsync` (`src/toimi.tools.tietue/Scheduling/TriggerRepository.cs:64-67`) sets `Enabled = true` without recomputing `NextFireAt`. Re-enabling an exhausted trigger yields `Enabled=true, NextFireAt=null` — invisible to `SchedulerTick`'s due query forever. `UpdateTriggerTool` has zero direct tests.

**Files:**
- Modify: `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs`
- Create: `src/toimi.tools.tietue.Tests/UpdateTriggerToolTests.cs`

- [ ] **Step 1: Read the test-infra patterns** — `src/toimi.tools.tietue.Tests/TestDb.cs`, the `TestConfig` helper (grep for `TestConfig.Default`), `TriggerRepositoryTests.cs`, and `SetTriggerToolTests.cs` — and mirror their setup (TestDb.New(), TypeRepository/EntityRepository seeding, `TriggerRepository(db, TestConfig.Default)`).

- [ ] **Step 2: Write the tests (failing one first)**

Create `src/toimi.tools.tietue.Tests/UpdateTriggerToolTests.cs`, mirroring the discovered setup. The tests (adapt constructor/seed calls to the actual patterns found in Step 1 — the assertions are the spec):

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class UpdateTriggerToolTests
{
  private const string Schema = /*lang=json,strict*/ """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";
  private static readonly DateTimeOffset Past = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

  private static async Task<(Data.TietueDbContext db, TriggerRepository triggers, UpdateTriggerTool tool, Guid entityId)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    var triggers = new TriggerRepository(db, TestConfig.Default);
    var tool = new UpdateTriggerTool(triggers);
    return (db, triggers, tool, e.Id);
  }

  [Fact]
  public async Task Invalid_guid_is_rejected()
  {
    var (db, _, tool, _) = await SetupAsync();
    using var _1 = db;
    Assert.Equal("Invalid id. Expected a GUID.", await tool.UpdateTrigger("not-a-guid"));
  }

  [Fact]
  public async Task Unknown_id_reports_not_found()
  {
    var (db, _, tool, _) = await SetupAsync();
    using var _1 = db;
    var id = Guid.NewGuid().ToString();
    Assert.Equal($"Trigger '{id}' not found.", await tool.UpdateTrigger(id));
  }

  [Fact]
  public async Task Handler_config_update_leaves_schedule_and_next_fire_untouched()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-01-01T09:00:00Z"}""", "notify", null, Past);
    var before = t.NextFireAt;

    await tool.UpdateTrigger(t.Id.ToString(), handlerConfig: /*lang=json,strict*/ """{"titleTemplate":"new"}""");

    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.Equal(/*lang=json,strict*/ """{"titleTemplate":"new"}""", updated.HandlerConfig);
    Assert.Equal(before, updated.NextFireAt);
  }

  [Fact]
  public async Task Reenabling_an_exhausted_trigger_never_yields_enabled_with_null_next_fire()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2026-01-02T09:00:00Z"}""", "notify", null, Past);
    // Simulate scheduler exhaustion: one-shot fired, disabled, no next fire.
    t.Enabled = false;
    t.NextFireAt = null;
    await db.SaveChangesAsync();

    var response = await tool.UpdateTrigger(t.Id.ToString(), enabled: true);

    // The invariant under test: never Enabled=true with NextFireAt=null (a
    // permanently dead-but-enabled trigger, invisible to the scheduler).
    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.False(updated.Enabled && updated.NextFireAt is null);
    // For a one-shot whose 'at' is in the past there is nothing to recompute:
    // the trigger must stay disabled and the tool response must say so.
    Assert.False(updated.Enabled);
    using var doc = JsonDocument.Parse(response);
    Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
  }

  [Fact]
  public async Task Reenabling_with_a_still_live_recurring_schedule_recomputes_next_fire()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(
      entityId, /*lang=json,strict*/ """{"start":"2026-01-01T09:00:00Z","rrule":"FREQ=DAILY","tz":"UTC"}""", "notify", null, Past);
    t.Enabled = false;
    t.NextFireAt = null;
    await db.SaveChangesAsync();

    await tool.UpdateTrigger(t.Id.ToString(), enabled: true);

    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.True(updated.Enabled);
    Assert.NotNull(updated.NextFireAt);
  }

  [Fact]
  public async Task Reenabling_a_paused_trigger_keeps_its_next_fire()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-06-01T09:00:00Z"}""", "notify", null, Past);
    var scheduled = t.NextFireAt;
    t.Enabled = false;
    await db.SaveChangesAsync();

    await tool.UpdateTrigger(t.Id.ToString(), enabled: true);

    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.True(updated.Enabled);
    Assert.Equal(scheduled, updated.NextFireAt);
  }
}
```

Note: `CreateAsync` validates/stamps schedules — if creating with a past `at` in `Reenabling_an_exhausted...` produces a null `NextFireAt` at create time already, that's fine; the manual `Enabled=false/NextFireAt=null` stamp makes the scenario deterministic either way. If `UpdateTriggerTool`'s constructor takes more dependencies than `(TriggerRepository)`, mirror the real signature.

- [ ] **Step 3: Verify the dead-trigger test fails**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter "FullyQualifiedName~UpdateTriggerToolTests"`
Expected: `Reenabling_an_exhausted...` FAILS (today it produces Enabled=true + NextFireAt=null) and `Reenabling_with_a_still_live...` FAILS (NextFireAt stays null). The other four PASS.

- [ ] **Step 4: Implement the fix**

In `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs`, in `UpdateAsync`, immediately after the `if (enabled is not null) { trigger.Enabled = enabled.Value; }` block:

```csharp
    // Re-enabling an exhausted trigger must not produce Enabled=true with a null
    // NextFireAt — such a trigger is invisible to the scheduler's due query forever.
    // Recompute from the schedule; if it still yields nothing, refuse to enable.
    if (trigger.Enabled && trigger.NextFireAt is null)
    {
      trigger.NextFireAt = Schedules.InitialNextFireAt(trigger.Schedule, now);
      trigger.Enabled = trigger.NextFireAt is not null;
    }
```

- [ ] **Step 5: Run full tietue suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj && mise exec dotnet -- dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj && mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue/Scheduling/TriggerRepository.cs src/toimi.tools.tietue.Tests/UpdateTriggerToolTests.cs
git commit -m "fix(tietue): recompute NextFireAt when re-enabling a trigger"
```

---

### Task 7: verkko — WebFetcher content-type fix (TDD) + HtmlExtractor pins

**Bug:** `src/toimi.tools.verkko/Fetcher/WebFetcher.cs:13-17` matches `contentType` with a case-sensitive `switch` on exactly `"text/html"`. `TEXT/HTML` (legal per RFC 9110) and `application/xhtml+xml` skip extraction and dump raw markup (scripts included) into model context. `HtmlExtractor` has zero tests.

**Files:**
- Modify: `src/toimi.tools.verkko/Fetcher/WebFetcher.cs`
- Create: `src/toimi.tools.verkko.Tests/WebFetcherTests.cs`
- Create: `src/toimi.tools.verkko.Tests/HtmlExtractorTests.cs`

- [ ] **Step 1: Write the failing WebFetcher test + pins**

Create `src/toimi.tools.verkko.Tests/WebFetcherTests.cs`:

```csharp
using System.Net;
using System.Text;
using toimi.tools.verkko.Fetcher;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class WebFetcherTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(respond(request));
    }
  }

  private static WebFetcher Fetcher(Func<HttpRequestMessage, HttpResponseMessage> respond)
  {
    return new WebFetcher(new HttpClient(new StubHandler(respond)));
  }

  private const string Html = "<html><body><script>var x = 1;</script><p>Real content</p></body></html>";

  [Theory]
  [InlineData("text/html")]
  [InlineData("TEXT/HTML")]
  [InlineData("application/xhtml+xml")]
  public async Task Html_media_types_are_extracted_regardless_of_case(string mediaType)
  {
    // Media-type case is insensitive per RFC 9110; a server sending TEXT/HTML
    // must not get raw markup (scripts included) dumped into model context.
    var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(Html, Encoding.UTF8, mediaType),
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.Contains("Real content", result.Content);
    Assert.DoesNotContain("var x = 1", result.Content);
  }

  [Fact]
  public async Task Missing_content_type_passes_raw_body_through_as_unknown()
  {
    var fetcher = Fetcher(_ =>
    {
      var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("plain body") };
      response.Content.Headers.ContentType = null;
      return response;
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.Equal("unknown", result.ContentType);
    Assert.Equal("plain body", result.Content);
  }

  [Fact]
  public async Task Non_success_status_returns_body_without_throwing()
  {
    var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
    {
      Content = new StringContent("upstream sad"),
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.Equal(502, result.StatusCode);
    Assert.Equal("upstream sad", result.Content);
  }

  [Fact]
  public async Task Overlong_content_is_truncated_with_a_marker()
  {
    var fetcher = Fetcher(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(new string('a', 60_000)),
    });

    var result = await fetcher.FetchAsync("http://example.test/", default);

    Assert.EndsWith("[Content truncated]", result.Content);
    Assert.True(result.Content.Length < 60_000);
  }
}
```

Adapt `FetchResult` member names (`Content`, `ContentType`, `StatusCode`) to the actual record in `src/toimi.tools.verkko/Fetcher/FetchResult.cs` — read it first.

- [ ] **Step 2: Verify the case-variant rows fail**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --filter "FullyQualifiedName~WebFetcherTests"`
Expected: `TEXT/HTML` and `application/xhtml+xml` rows FAIL (raw markup passed through, `var x = 1` present); `text/html` row and the other tests PASS.

- [ ] **Step 3: Fix WebFetcher**

Replace the switch in `src/toimi.tools.verkko/Fetcher/WebFetcher.cs`:

```csharp
    // Media-type case is insensitive (RFC 9110), and XHTML deserves the same
    // extraction; match loosely rather than on the exact lowercase literal.
    var content = contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
      ? HtmlExtractor.ExtractText(raw)
      : raw;
```

- [ ] **Step 4: Write the HtmlExtractor pins**

Create `src/toimi.tools.verkko.Tests/HtmlExtractorTests.cs`:

```csharp
using toimi.tools.verkko.Fetcher;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class HtmlExtractorTests
{
  [Fact]
  public void Script_style_and_nav_content_is_stripped()
  {
    var text = HtmlExtractor.ExtractText(
      "<body><script>var a=1;</script><style>.x{}</style><nav>menu</nav><p>keep me</p></body>");

    Assert.Contains("keep me", text);
    Assert.DoesNotContain("var a=1", text);
    Assert.DoesNotContain(".x{}", text);
    Assert.DoesNotContain("menu", text);
  }

  [Fact]
  public void Main_element_is_preferred_over_body_noise()
  {
    var text = HtmlExtractor.ExtractText(
      "<body><div>sidebar junk</div><main><p>the article</p></main></body>");

    Assert.Contains("the article", text);
    Assert.DoesNotContain("sidebar junk", text);
  }

  [Fact]
  public void Block_elements_produce_line_breaks_not_run_together_text()
  {
    var text = HtmlExtractor.ExtractText("<body><p>first para</p><p>second para</p></body>");

    Assert.DoesNotContain("parasecond", text.Replace(" ", ""));
    var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    Assert.Contains(lines, l => l.Contains("first para"));
    Assert.Contains(lines, l => l.Contains("second para"));
  }

  [Fact]
  public void Html_entities_are_decoded()
  {
    var text = HtmlExtractor.ExtractText("<body><p>fish &amp; chips</p></body>");

    Assert.Contains("fish & chips", text);
  }
}
```

These are pins — expected to PASS; if one fails, pin observed behavior and report.

- [ ] **Step 5: Full verkko suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj && mise exec dotnet -- dotnet format src/toimi.tools.verkko/toimi.tools.verkko.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj && mise exec dotnet -- dotnet format src/toimi.tools.verkko.Tests/toimi.tools.verkko.Tests.csproj --verify-no-changes
git add src/toimi.tools.verkko/Fetcher/WebFetcher.cs src/toimi.tools.verkko.Tests/
git commit -m "fix(verkko): extract HTML for any html media type; pin HtmlExtractor"
```

---

### Task 8: koti — ListEntities resilience (TDD fixes)

**Bugs in `src/toimi.tools.koti/Tools/ListEntitiesTool.cs`:**
1. `GetEntityAreasAsync` failure (template API 403/timeout — common for restricted tokens) throws out of the tool: listing entities fails completely even when areas are irrelevant.
2. A malformed entity in `/api/states` (missing `entity_id` or `state`) throws `KeyNotFoundException`, killing the whole listing.
3. A non-array `/api/states` response throws `InvalidOperationException`.

**Files:**
- Modify: `src/toimi.tools.koti/Tools/ListEntitiesTool.cs`
- Create: `src/toimi.tools.koti.Tests/ListEntitiesToolTests.cs`

- [ ] **Step 1: Write the tests**

Create `src/toimi.tools.koti.Tests/ListEntitiesToolTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using toimi.tools.koti.HomeAssistant;
using toimi.tools.koti.Tools;
using Xunit;

namespace toimi.tools.koti.Tests;

public class ListEntitiesToolTests
{
  private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      return Task.FromResult(respond(request));
    }
  }

  private const string States = /*lang=json,strict*/ """
    [
      {"entity_id":"light.kitchen","state":"on","attributes":{"friendly_name":"Kitchen light"}},
      {"entity_id":"light.hall","state":"off","attributes":{}},
      {"entity_id":"sensor.temp","state":"21.5","attributes":{"friendly_name":"Temp"}}
    ]
    """;

  private static HttpResponseMessage Json(string body)
  {
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
  }

  private static HttpResponseMessage Text(string body)
  {
    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
  }

  private static ListEntitiesTool Tool(string statesJson, Func<HttpResponseMessage> templateResponse)
  {
    var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
    {
      "/api/states" => Json(statesJson),
      "/api/template" => templateResponse(),
      _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });
    var client = new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = "http://ha.test:8123", BearerToken = "token" });
    return new ListEntitiesTool(client);
  }

  [Fact]
  public async Task Windows_line_endings_in_the_template_response_still_resolve_areas()
  {
    // HA can emit \r\n; a regression that stops trimming \r makes EVERY area
    // lookup miss silently — total, invisible loss of area assignment.
    var tool = Tool(States, () => Text("light.kitchen|Keittiö\r\nlight.hall|\r\nsensor.temp|Olohuone\r\n"));

    var result = await tool.ListEntities();

    using var doc = JsonDocument.Parse(result);
    var byId = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("entity_id").GetString()!);
    Assert.Equal("Keittiö", byId["light.kitchen"].GetProperty("area").GetString());
    Assert.Equal(JsonValueKind.Null, byId["light.hall"].GetProperty("area").ValueKind); // empty area excluded
    Assert.Equal("Olohuone", byId["sensor.temp"].GetProperty("area").GetString());
  }

  [Fact]
  public async Task Template_api_failure_degrades_to_null_areas_when_no_area_filter()
  {
    // Restricted tokens commonly get 403 on /api/template while /api/states works.
    // Listing lights must not die because area resolution is unavailable.
    var tool = Tool(States, () => new HttpResponseMessage(HttpStatusCode.Forbidden));

    var result = await tool.ListEntities(domain: "light");

    using var doc = JsonDocument.Parse(result);
    Assert.Equal(2, doc.RootElement.GetArrayLength());
    Assert.All(doc.RootElement.EnumerateArray(),
      e => Assert.Equal(JsonValueKind.Null, e.GetProperty("area").ValueKind));
  }

  [Fact]
  public async Task Template_api_failure_with_an_area_filter_reports_the_failure()
  {
    // With an area filter, degrading to "no areas" would return [] — indistinguishable
    // from a genuinely empty room. Say what actually happened instead.
    var tool = Tool(States, () => new HttpResponseMessage(HttpStatusCode.Forbidden));

    var result = await tool.ListEntities(area: "Keittiö");

    Assert.DoesNotContain("[", result); // not a JSON listing
    Assert.Contains("Area lookup failed", result);
  }

  [Fact]
  public async Task Malformed_entities_are_skipped_not_fatal()
  {
    const string mixed = /*lang=json,strict*/ """
      [
        {"no_entity_id_here":true},
        {"entity_id":"light.ok","state":"on","attributes":{}}
      ]
      """;
    var tool = Tool(mixed, () => Text(""));

    var result = await tool.ListEntities();

    using var doc = JsonDocument.Parse(result);
    var only = Assert.Single(doc.RootElement.EnumerateArray().ToList());
    Assert.Equal("light.ok", only.GetProperty("entity_id").GetString());
  }

  [Fact]
  public async Task Non_array_states_response_reports_an_error_instead_of_throwing()
  {
    var tool = Tool(/*lang=json,strict*/ """{"message":"API running."}""", () => Text(""));

    var result = await tool.ListEntities();

    Assert.Contains("Unexpected response", result);
  }

  [Fact]
  public async Task Area_filter_is_case_insensitive_but_exact()
  {
    var tool = Tool(States, () => Text("light.kitchen|Keittiö\nsensor.temp|Olohuone\n"));

    using var lower = JsonDocument.Parse(await tool.ListEntities(area: "keittiö"));
    Assert.Equal("light.kitchen", Assert.Single(lower.RootElement.EnumerateArray().ToList()).GetProperty("entity_id").GetString());

    using var partial = JsonDocument.Parse(await tool.ListEntities(area: "Keit"));
    Assert.Equal(0, partial.RootElement.GetArrayLength());
  }

  [Fact]
  public async Task Exactly_limit_matches_is_not_marked_truncated()
  {
    var tool = Tool(States, () => Text(""));

    var atLimit = await tool.ListEntities(domain: "light", limit: 2);
    Assert.DoesNotContain("[truncated", atLimit);

    var overLimit = await tool.ListEntities(domain: "light", limit: 1);
    Assert.Contains("[truncated at 1 entities", overLimit);
  }
}
```

- [ ] **Step 2: Verify the resilience tests fail**

Run: `mise exec dotnet -- dotnet test src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj --filter "FullyQualifiedName~ListEntitiesToolTests"`
Expected failures: both template-failure tests (HttpRequestException escapes), malformed-entity (KeyNotFoundException), non-array (InvalidOperationException). Expected passes: line-endings, case-insensitive filter, truncation boundary.

- [ ] **Step 3: Implement the fixes**

In `src/toimi.tools.koti/Tools/ListEntitiesTool.cs`, replace the body's opening (from `limit = Math.Clamp...` through `var prefix = ...`) with:

```csharp
    limit = Math.Clamp(limit, 1, 500);
    var states = await ha.GetStatesAsync();
    if (states.ValueKind != JsonValueKind.Array)
    {
      return "Unexpected response from Home Assistant when listing entities.";
    }

    Dictionary<string, string> areas;
    try
    {
      areas = await ha.GetEntityAreasAsync();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
      if (area is not null)
      {
        // Degrading to "no areas" here would return [] — indistinguishable from a
        // genuinely empty room. Report the real cause instead.
        return "Area lookup failed (template API unavailable) — cannot filter by area right now. Retry without the area filter.";
      }

      areas = [];
    }

    var prefix = domain is not null ? domain + "." : null;
```

And inside the loop, replace the `entityId` and `state` reads:

```csharp
    foreach (var entity in states.EnumerateArray())
    {
      if (!entity.TryGetProperty("entity_id", out var idProperty) || idProperty.GetString() is not { } entityId)
      {
        continue; // malformed entity — skip it rather than failing the whole listing
      }
```

and

```csharp
      var state = entity.TryGetProperty("state", out var stateProperty) ? stateProperty.GetString() : null;
```

Everything else (filters, truncation, serialization) stays as-is.

- [ ] **Step 4: Run full koti suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.koti/toimi.tools.koti.csproj && mise exec dotnet -- dotnet format src/toimi.tools.koti/toimi.tools.koti.csproj --verify-no-changes
mise exec dotnet -- dotnet format src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj && mise exec dotnet -- dotnet format src/toimi.tools.koti.Tests/toimi.tools.koti.Tests.csproj --verify-no-changes
git add src/toimi.tools.koti/Tools/ListEntitiesTool.cs src/toimi.tools.koti.Tests/ListEntitiesToolTests.cs
git commit -m "fix(koti): make ListEntities resilient to template failures and malformed entities"
```

---

### Task 9: tietue — stale-claim boundary + no-handler pins (pure pinning)

Two scheduler-correctness pins: the 15-minute stale-claim boundary in `EntityEventStore.TryClaimAsync` (`src/toimi.tools.tietue/Events/EntityEventStore.cs:80`, condition `existing.CreatedAt > now - StaleClaimAfter`), and `SchedulerTick`'s unregistered-handler branch (records an error event, trigger still advances — guards triggers persisted by an older build referencing a removed handler).

Deliberately NOT attempted: a concurrency test of the tick-lock/stale-takeover coupling — EF InMemory offers no real concurrency semantics, so such a test would be theater. The coupling stays documented by the comment at `EntityEventStore.cs:86-88`.

**Files:**
- Modify: `src/toimi.tools.tietue.Tests/EntityEventStoreTests.cs` (append boundary theory)
- Modify: `src/toimi.tools.tietue.Tests/SchedulerTickTests.cs` (append no-handler test)

- [ ] **Step 1: Read both existing test files** to mirror their setup helpers exactly (TestDb, event-seeding style, SchedulerTick construction — see also `ClaimThenRunTests.cs` for the due-trigger setup pattern).

- [ ] **Step 2: Append the boundary theory to EntityEventStoreTests**

Adapt the seeding to the file's existing style; the spec is:

```csharp
  [Theory]
  [InlineData(899, false)] // 14m59s old: still in progress, claim refused
  [InlineData(900, true)]  // exactly 15m: stale, taken over
  [InlineData(901, true)]  // past 15m: stale, taken over
  public async Task Stale_claim_boundary_is_exactly_fifteen_minutes(int ageSeconds, bool expectTakeover)
  {
    var db = TestDb.New();
    using var _ = db;
    var store = new EntityEventStore(db);
    var entityId = Guid.NewGuid();
    var occurrence = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    var now = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);
    db.EntityEvents.Add(new Data.EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = occurrence,
      Kind = "notify",
      Status = "started",
      CreatedAt = now - TimeSpan.FromSeconds(ageSeconds),
    });
    await db.SaveChangesAsync();

    var result = await store.TryClaimAsync(entityId, occurrence, "notify", now);

    // An off-by-one here means either a duplicate handler run (too eager) or a
    // permanently wedged occurrence (too lazy) when an instance crashes mid-run.
    Assert.Equal(expectTakeover ? ClaimResult.Claimed : ClaimResult.InProgress, result);
  }
```

Note: `EntityEvent` may live in a different namespace than `Data` — mirror what `ClaimThenRunTests.cs` uses. If `TryClaimAsync` requires an existing entity row (FK), seed one the way the file's other tests do.

- [ ] **Step 3: Append the no-handler test to SchedulerTickTests**

Mirror the due-trigger setup from `ClaimThenRunTests.SetupWithDueTriggerAsync`, but construct the registry EMPTY (`new HandlerRegistry([])`):

```csharp
  [Fact]
  public async Task Unregistered_handler_kind_records_an_error_and_still_advances_the_trigger()
  {
    // A trigger persisted by an older build can reference a handler that no longer
    // exists. It must not wedge the scheduler: error event recorded, trigger advances
    // (a one-shot is consumed/disabled).
    // [setup: TestDb, define type, create entity, create 'notify' trigger due in the
    //  past — exactly like ClaimThenRunTests — but HandlerRegistry([]) so 'notify'
    //  resolves to null]

    await tick.RunDueAsync(tickTime, default);

    var evt = await db.EntityEvents.SingleAsync(e => e.EntityId == entityId && e.Kind == "notify");
    Assert.Equal("error", evt.Status);
    Assert.Contains("no handler registered", evt.Result);
    var trigger = (await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(entityId))[0];
    Assert.False(trigger.Enabled); // one-shot consumed, not wedged
    Assert.NotNull(trigger.LastFiredAt);
  }
```

(The bracketed setup comment is a placeholder for you to fill with the file's real helper calls — the assertions are the spec.)

- [ ] **Step 4: Run, verify pins pass, full suite, format, commit**

```bash
mise exec dotnet -- dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj
mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj && mise exec dotnet -- dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes
git add src/toimi.tools.tietue.Tests/EntityEventStoreTests.cs src/toimi.tools.tietue.Tests/SchedulerTickTests.cs
git commit -m "test(tietue): pin stale-claim boundary and unregistered-handler advance"
```

If either pin FAILS against current behavior (e.g. the boundary is actually `>=`), that's a finding: pin the observed behavior and report DONE_WITH_CONCERNS so the controller can decide if it's a bug.

---

### Final verification (after all tasks)

- [ ] `mise exec dotnet -- dotnet test toimi.sln --nologo -v q` — all projects pass (394 baseline + the new tests).
- [ ] `cd src/toimi.web/ClientApp && mise exec node -- npm test` — 2/2 (unchanged by this plan).
- [ ] `mise exec dotnet -- dotnet format toimi.sln --verify-no-changes` — exit 0.

### Known deferred items (NOT in this plan)

PostgresTickLock integration tests (needs real Postgres), McpToolAggregator collision/reconnect seams (needs an MCP test server or refactor), ResilientMcpTool (needs InternalsVisibleTo — bundle with a future core change), verkko FetchCache TimeProvider seam, UrlGuard routable-filter extraction, koti CallService/GetHistory error-message hardening (Tier 3), frontend ToolCallList component tests.
