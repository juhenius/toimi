# ConversationContext Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace `ToimiAgent`'s raw `List<ChatMessage>` and its three textual/positional conventions — `RefreshDynamicContext`'s "index 1 must be the dynamic system message" rule, `ContextManager`'s `"Summary of earlier conversation:"` prefix scan, and the comment-enforced "RecordUsage before assistant append" ordering — with a `ConversationContext` type in `toimi.core` that OWNS the transcript and makes those conventions unrepresentable: slots are fields, the dynamic context is regenerated from stored catalogs (never parsed), and budget anchoring is folded into the assistant append so the wrong order cannot be written.

**Architecture:** `ConversationContext` (new, `src/toimi.core/ConversationContext.cs`) holds four structural parts: a fixed `SystemPrompt` message, a `DynamicContext` message (rebuilt on demand from the skill/type catalogs it stores as fields + a `TimeProvider` clock), an optional `Summary` message (set only by compaction), and a `Window` list of exchanges (user/assistant plus any host-appended system context such as tietue's fenced entity payload). `ToChatMessages()` assembles a read-only snapshot in slot order for the LLM call and for `ToimiAgent.Messages`. Compaction (`CompactIfNeededAsync`) absorbs `ContextManager`'s algorithm — same trigger arithmetic, same 30 s summarization call, same graceful failure — but finds the summary by field, not prefix, deleting the fragile leading-run/prefix walk-back entirely. `ContextBudget` survives as a separate estimator, but its `RecordUsage` is driven from inside `ConversationContext.AppendAssistant(text, promptTokensAsSent)`. `ContextManager`, `ToimiClientFactory.CreateInitialMessages`, and `ToimiClientFactory.RefreshDynamicContext` are deleted; `ToimiClientFactory` keeps only `CreateRequestOptions`. `ToimiAgent`'s frozen public surface is unchanged; `Messages` keeps its `IReadOnlyList<ChatMessage>` type but becomes a per-call snapshot that cannot be downcast to a live mutable list (C2 review finding). Three small deferred C2 fixes ride along: explicit throw instead of `completed!` in `ToimiAgent.RunTurnAsync` and `ToimiHub.SendMessage`, dispose-on-leak in `ToimiHub.OnConnectedAsync`, and `Interlocked.CompareExchange` for `_turnInProgress`.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI (`ChatMessage`, `IChatClient`), BCL `TimeProvider` for clock injection, xUnit. No new packages. React client, SignalR surface, `IAgentRunner`, and EntityEvent shapes untouched.

## Global Constraints

- **dotnet is not on PATH.** Before every build/test/format command:
  `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"`
- **Test commands** (use `--filter 'FullyQualifiedName~<TestClass>'` while iterating, full suite before each commit):
  - `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj`
  - `dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj`
  - `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
  - Current counts: **core 84, web 38, tietue 312** (tietue includes docker-gated Testcontainers tests that skip without Docker; passed+skipped totals must not drop). End state minimums: **core ≥ 93, web ≥ 38 (exactly 38 expected), tietue ≥ 312**. Core arithmetic: −10 (`ContextManagerTests` deleted) −3 (`ToimiClientFactoryTests` deleted) +4 (`ContextBudgetTests`) +10 (`ConversationContextTests`) +8 (`ConversationContextCompactionTests`) = 93.
- **Frozen surfaces.** `ToimiAgent`: `StartAsync`/`SendAsync`/`RunTurnAsync`/`AppendMessage`/`DiscardLastAssistantMessage`/`Reset`/`DisposeAsync`/`ToolCount`/`SkillSummary`/`TypeCatalog` signatures unchanged; `Messages` stays `IReadOnlyList<ChatMessage>` (only its backing changes to a snapshot — every consumer is read-only, verified: `ToimiAgentTests` and nothing else reads it). SignalR client events, the React client, `EntityEvent` shapes, and `IAgentRunner` unchanged. `src/toimi.tools.tietue/Agents/AgentRunner.cs` is NOT modified.
- **`ToimiHubTests` (all 38 web tests) must pass with ZERO modifications** in Tasks 3–4 — no fake shape changes are needed by this plan; if one somehow becomes necessary, only mechanical adaptation is allowed, never assertion weakening. `InitialMessagesTests` gets exactly one mechanical retarget (Task 3) with assertions unchanged.
- **Compaction behavior must not change:** trigger condition (estimate ≥ maxTokens AND > 10 non-system messages AND ≥ 2 summarizable), what is preserved (system messages + the 10 most recent exchanges), summary replacement semantics (prior summary is folded into the next one, never accumulated), 30 s timeout, proceed-uncompacted on summarization failure, 300 k char input cap, 500-char tool-result truncation. Only WHERE the logic lives and HOW slots are found changes. One documented micro-deviation: see Design Decisions.
- **Formatting/lint:** for every touched project, before each commit run `dotnet format src/<proj>/<proj>.csproj` (apply) then `dotnet format src/<proj>/<proj>.csproj --verify-no-changes` (must exit 0; IDE0046 sometimes needs a manual fix — block bodies IDE0022 means no expression-bodied methods, expression-bodied properties are fine). 2-space indent, file-scoped namespaces. Commit style `<type>(<scope>): <subject>`.

## Design Decisions

- **Slots as fields.** `_systemPrompt` (readonly), `_dynamicContext` (replaced whole on refresh), `_summary` (nullable, written only by compaction/reset), `_window` (private `List<ChatMessage>`). Snapshot order: `[SystemPrompt, DynamicContext, Summary?, ...Window]`. No index arithmetic and no prefix scanning exist anywhere after this refactor.
- **Refresh regenerates instead of parsing.** `ConversationContext` stores `skillSummary`/`typeCatalog` as fields; `RefreshDynamicContext()` rebuilds the whole dynamic message from them + `TimeProvider.GetUtcNow()`. The old failure mode pinned by `ToimiClientFactoryTests` ("if `CreateInitialMessages` ever changes its layout, Refresh silently degrades to never updating the clock") is structurally impossible: there is no layout to match. A `TimeProvider? timeProvider = null` ctor parameter (defaults to `TimeProvider.System`) makes the clock testable without message surgery.
- **`ContextManager` is absorbed, not wrapped.** Its algorithm moves into `CompactIfNeededAsync` verbatim except for slot discovery: the prior summary is `_summary` (a field — the walk-back loop over `SummaryPrefix`-prefixed trailing system messages is deleted), and the protected system block is the two structural slots plus the leading run of System messages **in the window** (tietue's fenced entity payload — exactly what the old leading-run rule protected). Trigger arithmetic is preserved exactly: `nonSystemCount = (summary present ? 1 : 0) + windowNonLeadCount`, compact only if `nonSystemCount > 10` and `summarizeCount = nonSystemCount − 10 ≥ 2`; the prior summary is the first summarization input. The `"Summary of earlier conversation:"` prefix survives only as presentation text inside the summary message — it is never scanned.
- **Documented micro-deviation (summary position vs. window-leading system messages).** Old list order after compaction in the AgentRunner case was `[sys, dynamic, entityContext, summary, last-10]`; the new snapshot is `[sys, dynamic, summary, entityContext, last-10]` — the summary slot has a fixed home before the window. Both orders keep all system context ahead of the conversation; in the ToimiHub case (no window system messages) the order is byte-identical to today. Accepted: position among members of the protected block is not part of the pinned compaction behavior (what triggers, what is preserved, no accumulation — all unchanged).
- **Anchoring folded into the append.** `AppendAssistant(string text, int? promptTokensAsSent = null)` anchors `ContextBudget` to the transcript AS SENT (pre-append) and then appends — the wrong order is unwritable. `ContextBudget` stays public (tests drive it directly) but its production writer is `ConversationContext` only; its `List<ChatMessage>` parameters loosen to `IReadOnlyList<ChatMessage>` so snapshots flow in (source-compatible for every existing caller: `List<T>` implements `IReadOnlyList<T>`).
- **`Messages` becomes an un-downcastable snapshot.** `ToChatMessages()` returns `List<ChatMessage>.AsReadOnly()` built fresh per call: a stale reference stays frozen, and casting to `List<ChatMessage>` fails (`ReadOnlyCollection` wrapper), closing the C2 review's downcast hole. All existing `agent.Messages` consumers are read-only asserts, so `ToimiAgentTests` passes **unmodified** — that is Task 3's harness.
- **A generic `Append(ChatMessage)` overload exists** alongside `Append(ChatRole, string)`/`AppendUser`/`AppendAssistant`. `ToimiAgent.AppendMessage` uses the role+text form; the `ChatMessage` form is the seam that lets tests inject `FunctionCallContent`/`FunctionResultContent` messages, preserving the old `ContextManagerTests` coverage of tool-content summarization and estimation.
- **Test disposition (per class):**
  - `ContextManagerTests` (10 facts) — **deleted as a file, zero coverage lost**: the 4 pure-`ContextBudget` facts (`Estimate_without_anchor…`, `Estimate_with_anchor…`, `Estimate_clamps…`, `Estimate_counts_function_call_and_result_content`) move **verbatim** to new `ContextBudgetTests.cs` (Task 1); the 6 compaction facts are rewritten against `ConversationContext` in `ConversationContextCompactionTests` (Task 2) with the same assertions re-expressed through the new surface (raw-list shapes like "30 user messages, no system message" become "context + 30 appended users" — the always-present slots change expected counts, not semantics). Rewrite reason: the old tests construct list shapes (`messages[0]` a bare system message, summaries as in-list system messages) that no longer exist.
  - `ToimiClientFactoryTests` (3 facts) — **deleted as a file** (Task 3). `Refresh_replaces_only_the_time_line_and_preserves_the_catalogs` → replaced by `Refresh_updates_the_clock_and_preserves_the_catalogs` (clock-injected, stronger). `Refresh_is_a_silent_no_op_when_the_structure_does_not_match` → **deleted deliberately**: it pins the silent-degradation failure mode this refactor exists to make impossible; its replacement is `Refresh_cannot_silently_degrade_whatever_the_transcript_shape`, which asserts the opposite invariant. `Initial_messages_omit_absent_catalog_sections` → replaced by `Initial_layout_is_system_prompt_then_dynamic_context` + `Dynamic_context_omits_absent_catalog_sections`.
  - `ToimiAgentTests` (12 facts) — **survives byte-for-byte unmodified**; it is the red/green harness for the Task 3 rewire (verified above: every `Messages`/budget interaction works against the snapshot + folded anchoring).
  - `ToimiHubTests` (6 facts) + rest of web suite — **survives byte-for-byte unmodified** through Tasks 3 and 4; it is the harness for the hub hardening.
  - `InitialMessagesTests` (web, 3 facts) — **retargeted mechanically** from `ToimiClientFactory.CreateInitialMessages(...)` to `new ConversationContext(...).ToChatMessages()`, assertions unchanged (Task 3). Reconciliation of the survey-flagged duplication: catalog-**content** assertions now live only here (the consumer-facing pin that the hub's catalogs reach the prompt); core's `ConversationContextTests` pins slot **structure** and refresh invariants. Kept in web rather than deleted because the suite total must not drop.
- **Hub hardening is test-justified, not test-driven** (Task 4): the agent-leak dispose is unobservable through the hub's public seams (the agent is hub-internal; no fake intercepts `DisposeAsync` without changing production surface), and `?? throw` replaces a null-forgiveness on a path `SendAsync`'s contract makes unreachable — both changes ship under the unmodified 38-test web suite as a pure regression harness, stated here explicitly.

---

### Task 1: `ConversationContext` — slots, appends, refresh, reset, budget anchoring (TDD)

Pure addition: nothing existing is rewired yet (`ContextManager`/`ToimiClientFactory` keep working; `ToimiAgent` untouched). TDD: write `ConversationContextTests` red, implement green. The `ContextBudget` parameter loosening and the budget-test move are mechanical (existing suites prove them).

**Files**
- Create: `src/toimi.core/ConversationContext.cs`
- Create: `src/toimi.core.Tests/ConversationContextTests.cs`
- Create: `src/toimi.core.Tests/ContextBudgetTests.cs`
- Modify: `src/toimi.core/ContextBudget.cs` (params → `IReadOnlyList<ChatMessage>`)
- Modify: `src/toimi.core/ToimiClientFactory.cs` (one line: `private const string SystemPrompt` → `internal const string SystemPrompt`)
- Modify: `src/toimi.core.Tests/ContextManagerTests.cs` (remove the 4 moved budget facts)

**Interfaces**
- Produces:
  ```csharp
  public sealed class ConversationContext
  {
    public ConversationContext(string? skillSummary = null, string? typeCatalog = null,
      ContextBudget? budget = null, TimeProvider? timeProvider = null);
    public IReadOnlyList<ChatMessage> ToChatMessages();
    public void Append(ChatRole role, string text);
    public void Append(ChatMessage message);
    public void AppendUser(string text);
    public void AppendAssistant(string text, int? promptTokensAsSent = null);
    public void RefreshDynamicContext();
    public bool DiscardLastAssistantMessage();
    public void Reset();
    public int Estimate();
  }
  ```
- Changes: `ContextBudget.RecordUsage(int, IReadOnlyList<ChatMessage>)`, `ContextBudget.Estimate(IReadOnlyList<ChatMessage>)`, `ContextBudget.TotalChars(IReadOnlyList<ChatMessage>)` (were `List<ChatMessage>`; source-compatible for all callers).
- Consumes: `ToimiClientFactory.SystemPrompt` (made `internal` this task; moves fully in Task 3), `ContextBudget`, BCL `TimeProvider`.

**Steps**

- [ ] Write `src/toimi.core.Tests/ConversationContextTests.cs`:

  ```csharp
  using Microsoft.Extensions.AI;
  using Xunit;

  namespace Toimi.Core.Tests;

  public class ConversationContextTests
  {
    private sealed class FakeTime : TimeProvider
    {
      public DateTimeOffset Now { get; set; } = new(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);

      public override DateTimeOffset GetUtcNow()
      {
        return Now;
      }
    }

    [Fact]
    public void Initial_layout_is_system_prompt_then_dynamic_context()
    {
      var messages = new ConversationContext().ToChatMessages();

      Assert.Equal(2, messages.Count);
      Assert.All(messages, m => Assert.Equal(ChatRole.System, m.Role));
      Assert.Contains("You are Toimi", messages[0].Text ?? "");
      Assert.StartsWith("Current time: ", messages[1].Text ?? "");
    }

    [Fact]
    public void Dynamic_context_omits_absent_catalog_sections()
    {
      var messages = new ConversationContext(skillSummary: null, typeCatalog: null).ToChatMessages();

      var context = messages[1].Text ?? "";
      Assert.DoesNotContain("Available skills", context);
      Assert.DoesNotContain("Available data types", context);
    }

    [Fact]
    public void Refresh_updates_the_clock_and_preserves_the_catalogs()
    {
      var time = new FakeTime();
      var context = new ConversationContext("skillA — does things", "typeB — a schema", timeProvider: time);
      Assert.Contains("1999", context.ToChatMessages()[1].Text ?? "");

      time.Now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
      context.RefreshDynamicContext();

      var refreshed = context.ToChatMessages()[1].Text ?? "";
      Assert.DoesNotContain("1999", refreshed);
      Assert.StartsWith("Current time: 2026-08-05 12:00", refreshed);
      // The catalogs must survive the refresh — losing them mid-session silently
      // strips the model's knowledge of available skills and types.
      Assert.Contains("Available skills", refreshed);
      Assert.Contains("skillA", refreshed);
      Assert.Contains("Available data types", refreshed);
      Assert.Contains("typeB", refreshed);
    }

    [Fact]
    public void Refresh_cannot_silently_degrade_whatever_the_transcript_shape()
    {
      // The old ToimiClientFactory.RefreshDynamicContext located the clock by
      // index-1 + "Current time: " prefix and silently no-opped on any other
      // shape (pinned by the deleted ToimiClientFactoryTests). The slot is a
      // field now: refresh works no matter what the window holds.
      var time = new FakeTime();
      var context = new ConversationContext(timeProvider: time);
      context.Append(ChatRole.System, "fenced entity context");
      context.AppendUser("hi");
      context.AppendAssistant("hello");

      time.Now = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);
      context.RefreshDynamicContext();

      Assert.StartsWith("Current time: 2026-08-05 12:00", context.ToChatMessages()[1].Text ?? "");
    }

    [Fact]
    public void Appends_land_in_order_after_the_slots()
    {
      var context = new ConversationContext();
      context.Append(ChatRole.System, "entity data");
      context.AppendUser("question");
      context.AppendAssistant("answer");

      var messages = context.ToChatMessages();
      Assert.Equal(5, messages.Count);
      Assert.Equal("entity data", messages[2].Text);
      Assert.Equal(ChatRole.User, messages[3].Role);
      Assert.Equal("question", messages[3].Text);
      Assert.Equal(ChatRole.Assistant, messages[4].Role);
      Assert.Equal("answer", messages[4].Text);
    }

    [Fact]
    public void AppendAssistant_anchors_the_budget_before_the_append()
    {
      var budget = new ContextBudget();
      var context = new ConversationContext(budget: budget);
      context.AppendUser("q");

      context.AppendAssistant("123456789", promptTokensAsSent: 100);

      // Anchored to the transcript AS SENT (before the 9-char reply was appended):
      // estimate = 100 + 9/3. If the anchor were taken after the append, the
      // estimate would be a flat 100 — undercounting by one response. The old
      // code enforced this ordering by comment; now it is unwritable.
      Assert.Equal(103, context.Estimate());
    }

    [Fact]
    public void AppendAssistant_without_usage_leaves_the_estimate_on_chars_over_4()
    {
      var context = new ConversationContext();
      context.AppendUser("q");
      context.AppendAssistant("hello");

      Assert.Equal(ContextBudget.TotalChars(context.ToChatMessages()) / 4, context.Estimate());
    }

    [Fact]
    public void Discard_removes_only_a_trailing_assistant_message_and_never_a_slot()
    {
      var context = new ConversationContext();
      Assert.False(context.DiscardLastAssistantMessage()); // empty window: the slots are untouchable
      Assert.Equal(2, context.ToChatMessages().Count);

      context.AppendUser("q");
      Assert.False(context.DiscardLastAssistantMessage()); // trailing user: no-op
      Assert.Equal(3, context.ToChatMessages().Count);

      context.AppendAssistant("a");
      Assert.True(context.DiscardLastAssistantMessage());
      Assert.Equal(ChatRole.User, context.ToChatMessages()[^1].Role);
    }

    [Fact]
    public void Reset_clears_window_and_budget_but_keeps_the_catalogs()
    {
      var budget = new ContextBudget();
      var context = new ConversationContext("skillA", "typeB", budget);
      context.AppendUser("q");
      context.AppendAssistant("a", promptTokensAsSent: 500);

      context.Reset();

      var messages = context.ToChatMessages();
      Assert.Equal(2, messages.Count);
      Assert.Contains("skillA", messages[1].Text ?? "");
      Assert.Contains("typeB", messages[1].Text ?? "");
      // Anchor cleared: back to the plain chars/4 heuristic.
      Assert.Equal(ContextBudget.TotalChars(messages) / 4, context.Estimate());
    }

    [Fact]
    public void ToChatMessages_returns_an_immutable_snapshot()
    {
      var context = new ConversationContext();
      var before = context.ToChatMessages();

      context.AppendUser("added later");

      Assert.Equal(2, before.Count); // an earlier snapshot stays frozen
      Assert.Equal(3, context.ToChatMessages().Count);
      // The C2 review flagged that Messages could be downcast to the live list
      // and mutated behind the agent's back — the snapshot must not be a List.
      Assert.IsNotType<List<ChatMessage>>(before);
    }
  }
  ```

- [ ] Run it red (compile error = red):
  `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter 'FullyQualifiedName~ConversationContextTests'`

- [ ] In `src/toimi.core/ToimiClientFactory.cs`, change the const's visibility (content untouched — the full move happens in Task 3):

  ```csharp
  // Stable identity and behavior policies. Rarely changes.
  internal const string SystemPrompt = """
  ```

- [ ] In `src/toimi.core/ContextBudget.cs`, loosen the three signatures (bodies unchanged — `Sum` works on any enumerable):
  - `public void RecordUsage(int promptTokens, IReadOnlyList<ChatMessage> messages)`
  - `public int Estimate(IReadOnlyList<ChatMessage> messages)`
  - `public static int TotalChars(IReadOnlyList<ChatMessage> messages)`

- [ ] Create `src/toimi.core/ConversationContext.cs`:

  ```csharp
  using Microsoft.Extensions.AI;

  namespace Toimi.Core;

  /// <summary>
  /// The conversation transcript as a structured type. Four parts, in snapshot
  /// order: a fixed SystemPrompt, a DynamicContext message (regenerated from the
  /// stored skill/type catalogs + the clock — never parsed), an optional Summary
  /// (written only by compaction), and the Window of exchanges. The old
  /// conventions — "index 1 is the dynamic message", "the summary is whatever
  /// starts with the magic prefix", "RecordUsage must run before the assistant
  /// append" — are unrepresentable here: slots are fields and the budget anchor
  /// is taken inside <see cref="AppendAssistant"/>.
  /// </summary>
  public sealed class ConversationContext
  {
    private readonly ChatMessage _systemPrompt;
    private readonly string? _skillSummary;
    private readonly string? _typeCatalog;
    private readonly ContextBudget _budget;
    private readonly TimeProvider _time;
    private readonly List<ChatMessage> _window = [];
    private ChatMessage _dynamicContext;
    private ChatMessage? _summary;

    public ConversationContext(
      string? skillSummary = null, string? typeCatalog = null,
      ContextBudget? budget = null, TimeProvider? timeProvider = null)
    {
      _skillSummary = skillSummary;
      _typeCatalog = typeCatalog;
      _budget = budget ?? new ContextBudget();
      _time = timeProvider ?? TimeProvider.System;
      _systemPrompt = new ChatMessage(ChatRole.System, ToimiClientFactory.SystemPrompt);
      _dynamicContext = BuildDynamicContext();
    }

    /// <summary>
    /// Read-only snapshot in slot order: [SystemPrompt, DynamicContext, Summary?,
    /// ...Window]. Built fresh per call — a held reference stays frozen and cannot
    /// be downcast to a mutable list.
    /// </summary>
    public IReadOnlyList<ChatMessage> ToChatMessages()
    {
      var result = new List<ChatMessage>(2 + (_summary is null ? 0 : 1) + _window.Count)
      {
        _systemPrompt,
        _dynamicContext,
      };
      if (_summary is not null)
      {
        result.Add(_summary);
      }

      result.AddRange(_window);
      return result.AsReadOnly();
    }

    public void Append(ChatRole role, string text)
    {
      _window.Add(new ChatMessage(role, text));
    }

    /// <summary>Window append of a pre-built message (e.g. tool-content messages in tests).</summary>
    public void Append(ChatMessage message)
    {
      _window.Add(message);
    }

    public void AppendUser(string text)
    {
      Append(ChatRole.User, text);
    }

    /// <summary>
    /// Appends the assistant response. When the provider reported real usage,
    /// anchors the budget to the transcript AS SENT (i.e. before this append), so
    /// the response counts into the chars-delta and the estimate stays
    /// conservative — the anchor-before-append ordering is internal and cannot be
    /// done wrong by a caller.
    /// </summary>
    public void AppendAssistant(string text, int? promptTokensAsSent = null)
    {
      if (promptTokensAsSent is int promptTokens)
      {
        _budget.RecordUsage(promptTokens, ToChatMessages());
      }

      Append(ChatRole.Assistant, text);
    }

    /// <summary>Regenerates the dynamic context (clock + catalogs) from the stored fields.</summary>
    public void RefreshDynamicContext()
    {
      _dynamicContext = BuildDynamicContext();
    }

    /// <summary>Estimated prompt tokens for the current transcript (budget-anchored when usage was recorded).</summary>
    public int Estimate()
    {
      return _budget.Estimate(ToChatMessages());
    }

    /// <summary>
    /// Removes a trailing assistant message from the window (for hosts whose
    /// persist of the assistant message failed). The slots are structurally out of
    /// reach. Returns false (no-op) when the window is empty or ends elsewhere.
    /// </summary>
    public bool DiscardLastAssistantMessage()
    {
      if (_window.Count > 0 && _window[^1].Role == ChatRole.Assistant)
      {
        _window.RemoveAt(_window.Count - 1);
        return true;
      }

      return false;
    }

    /// <summary>Fresh conversation: clears window + summary, regenerates the dynamic context, clears the budget anchor. Catalogs are kept.</summary>
    public void Reset()
    {
      _window.Clear();
      _summary = null;
      _dynamicContext = BuildDynamicContext();
      _budget.Reset();
    }

    private ChatMessage BuildDynamicContext()
    {
      var context = new System.Text.StringBuilder();
      context.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
        $"Current time: {_time.GetUtcNow():yyyy-MM-dd HH:mm} UTC (Europe/Helsinki is UTC+2 or UTC+3 during DST)");

      if (!string.IsNullOrEmpty(_skillSummary))
      {
        context.AppendLine();
        context.AppendLine("Available skills (use GetSkill for full instructions):");
        context.AppendLine(_skillSummary);
      }

      if (!string.IsNullOrEmpty(_typeCatalog))
      {
        context.AppendLine();
        context.AppendLine("Available data types (use create/search/list with these type names; data must match the JSON schema):");
        context.AppendLine(_typeCatalog);
      }

      return new ChatMessage(ChatRole.System, context.ToString());
    }
  }
  ```

- [ ] Run green: `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter 'FullyQualifiedName~ConversationContextTests'` — 10 passing.

- [ ] Create `src/toimi.core.Tests/ContextBudgetTests.cs` by moving the four pure-budget facts **verbatim** out of `ContextManagerTests.cs` (they compile unchanged against the loosened signatures):

  ```csharp
  using Microsoft.Extensions.AI;
  using Xunit;

  namespace Toimi.Core.Tests;

  public class ContextBudgetTests
  {
    private static ChatMessage Text(ChatRole role, int chars)
    {
      return new ChatMessage(role, new string('x', chars));
    }

    [Fact]
    public void Estimate_without_anchor_falls_back_to_chars_over_4()
    {
      var budget = new ContextBudget();
      var messages = new List<ChatMessage> { Text(ChatRole.User, 4000) };

      Assert.Equal(1000, budget.Estimate(messages));
    }

    [Fact]
    public void Estimate_with_anchor_uses_real_tokens_plus_conservative_delta()
    {
      var budget = new ContextBudget();
      var messages = new List<ChatMessage> { Text(ChatRole.User, 4000) };
      budget.RecordUsage(2500, messages); // reality: denser than 4 chars/token

      messages.Add(Text(ChatRole.Assistant, 300));

      Assert.Equal(2500 + (300 / 3), budget.Estimate(messages));
    }

    [Fact]
    public void Estimate_clamps_when_messages_shrink_below_the_anchor()
    {
      // The hub's error path removes the last message after an anchor was recorded;
      // the delta must clamp to zero instead of going negative.
      var budget = new ContextBudget();
      var messages = new List<ChatMessage> { Text(ChatRole.User, 4000), Text(ChatRole.Assistant, 1000) };
      budget.RecordUsage(2500, messages);

      messages.RemoveAt(messages.Count - 1);

      Assert.Equal(2500, budget.Estimate(messages));
    }

    [Fact]
    public void Estimate_counts_function_call_and_result_content()
    {
      var budget = new ContextBudget();
      var payload = new string('r', 8000);
      var messages = new List<ChatMessage>
      {
        new(ChatRole.Assistant, [new FunctionCallContent("call1", "search", new Dictionary<string, object?> { ["query"] = "milk" })]),
        new(ChatRole.Tool, [new FunctionResultContent("call1", payload)]),
      };

      // Tool-only messages have no TextContent; the estimate must still see their bulk
      // or tool-heavy histories never trigger compaction.
      Assert.True(budget.Estimate(messages) >= payload.Length / 4);
    }
  }
  ```

- [ ] Delete those same four `[Fact]` methods from `src/toimi.core.Tests/ContextManagerTests.cs` (`Estimate_without_anchor_falls_back_to_chars_over_4`, `Estimate_with_anchor_uses_real_tokens_plus_conservative_delta`, `Estimate_clamps_when_messages_shrink_below_the_anchor`, `Estimate_counts_function_call_and_result_content`). The `Text` helper stays (the remaining compaction facts use it).

- [ ] Full core suite: `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj` — expect **94 passing** (84 − 4 moved + 4 moved back + 10 new = 94; nothing lost).
- [ ] Web + tietue untouched but verify no ripple: `dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj` (38) and `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` (312 incl. skips).
- [ ] Format: `dotnet format src/toimi.core/toimi.core.csproj && dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj && dotnet format src/toimi.core/toimi.core.csproj --verify-no-changes && dotnet format src/toimi.core.Tests/toimi.core.Tests.csproj --verify-no-changes`
- [ ] Commit: `git add -A && git commit -m "refactor(core): ConversationContext transcript type — slots, folded budget anchoring"` (append the Co-Authored-By trailer).

---

### Task 2: Absorb compaction into `ConversationContext.CompactIfNeededAsync` (TDD)

Still additive: `ContextManager` remains in place (ToimiAgent still calls it) — the duplicated algorithm lives for exactly one task and is deleted in Task 3. TDD: rewrite the six `ContextManagerTests` compaction facts against the new surface, red first.

**Files**
- Create: `src/toimi.core.Tests/ConversationContextCompactionTests.cs`
- Modify: `src/toimi.core/ConversationContext.cs` (add `CompactIfNeededAsync` + private helpers/consts)
- Test: `src/toimi.core.Tests/FakeChatClient.cs` (existing, unchanged — `GetResponseAsync` records requests, `NextResponseText`, `Throw`)

**Interfaces**
- Produces: `public Task<bool> CompactIfNeededAsync(IChatClient client, int maxTokens = 100_000, CancellationToken ct = default)` on `ConversationContext`.
- Consumes: `IChatClient.GetResponseAsync`, `ContextBudget.Reset()`.

**Steps**

- [ ] Write `src/toimi.core.Tests/ConversationContextCompactionTests.cs`:

  ```csharp
  using Microsoft.Extensions.AI;
  using Xunit;

  namespace Toimi.Core.Tests;

  public class ConversationContextCompactionTests
  {
    private static ConversationContext Filled(int userMessages, int charsEach = 100, ContextBudget? budget = null)
    {
      var context = new ConversationContext(budget: budget);
      for (var i = 0; i < userMessages; i++)
      {
        context.AppendUser(new string('x', charsEach));
      }

      return context;
    }

    [Fact]
    public async Task Compaction_replaces_older_window_messages_with_one_summary_and_keeps_the_slots()
    {
      var client = new FakeChatClient { NextResponseText = "the gist" };
      var context = Filled(30);

      var compacted = await context.CompactIfNeededAsync(client, maxTokens: 1);

      Assert.True(compacted);
      var messages = context.ToChatMessages();
      // Slots intact, summary in its slot, the 10 most recent window messages kept.
      Assert.Contains("You are Toimi", messages[0].Text ?? "");
      Assert.StartsWith("Current time: ", messages[1].Text ?? "");
      Assert.StartsWith("Summary of earlier conversation:", messages[2].Text ?? "");
      Assert.Contains("the gist", messages[2].Text ?? "");
      Assert.Equal(2 + 1 + 10, messages.Count);
    }

    [Fact]
    public async Task No_compaction_below_the_limit()
    {
      var client = new FakeChatClient();
      var context = Filled(1);

      var compacted = await context.CompactIfNeededAsync(client, maxTokens: 100_000);

      Assert.False(compacted);
      Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task Compaction_preserves_leading_window_system_messages()
    {
      // AgentRunner appends the fenced entity payload as a window-leading System
      // message; compaction must protect it exactly like the old leading-run rule.
      var client = new FakeChatClient();
      var context = new ConversationContext();
      context.Append(ChatRole.System, "<entity_data>payload</entity_data>");
      for (var i = 0; i < 30; i++)
      {
        context.AppendUser(new string('x', 100));
      }

      Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

      var messages = context.ToChatMessages();
      Assert.Contains(messages, m => m.Role == ChatRole.System && (m.Text ?? "").Contains("<entity_data>"));
      // ...and it was protected FROM summarization, not summarized away.
      var summaryInput = string.Join("\n", Assert.Single(client.Requests).Select(m => m.Text));
      Assert.DoesNotContain("<entity_data>", summaryInput);
    }

    [Fact]
    public async Task Compaction_includes_tool_calls_and_results_in_summary_input()
    {
      var client = new FakeChatClient();
      var context = new ConversationContext();
      for (var i = 0; i < 20; i++)
      {
        context.AppendUser(new string('x', 10));
        context.Append(new ChatMessage(ChatRole.Assistant, [
          new FunctionCallContent($"call{i}", "search", new Dictionary<string, object?> { ["query"] = "milk" }),
        ]));
        context.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", "found 3 items")]));
      }

      Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

      var payload = string.Join("\n", Assert.Single(client.Requests).Select(m => m.Text));
      Assert.Contains("search", payload);       // tool call name present
      Assert.Contains("found 3 items", payload); // tool result present
    }

    [Fact]
    public async Task Compaction_resets_the_budget_anchor()
    {
      var client = new FakeChatClient();
      var budget = new ContextBudget();
      var context = Filled(30, budget: budget);
      // Absurd anchor forces compaction and must then be discarded.
      context.AppendAssistant("a", promptTokensAsSent: 999_999);

      Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 100_000));

      // Anchor gone: the estimate is chars/4 of the compacted transcript.
      Assert.True(context.Estimate() < 999_999);
    }

    [Fact]
    public async Task Compaction_that_fails_to_summarize_proceeds_uncompacted()
    {
      var client = new FakeChatClient { Throw = true };
      var context = Filled(30);
      var before = context.ToChatMessages().Count;

      var compacted = await context.CompactIfNeededAsync(client, maxTokens: 1);

      Assert.False(compacted);
      Assert.Equal(before, context.ToChatMessages().Count); // untouched on failure
    }

    [Fact]
    public async Task Second_compaction_folds_the_prior_summary_instead_of_accumulating()
    {
      var client = new FakeChatClient { NextResponseText = "first summary gist" };
      var context = Filled(40);
      Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

      client.NextResponseText = "second summary gist";
      for (var i = 0; i < 20; i++)
      {
        context.AppendUser(new string('x', 100));
      }

      Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 1));

      // The old summary must be summarized INTO the new one, not kept beside it —
      // otherwise every compaction leaves one more permanent System message and
      // the reclaimable window shrinks to nothing.
      var messages = context.ToChatMessages();
      Assert.Equal(1, messages.Count(m =>
        m.Role == ChatRole.System && (m.Text ?? "").StartsWith("Summary of earlier conversation:", StringComparison.Ordinal)));
      Assert.Contains("second summary gist", messages[2].Text ?? "");
      Assert.Contains("first summary gist", string.Join("\n", client.Requests[^1].Select(m => m.Text)));
      Assert.Contains("You are Toimi", messages[0].Text ?? ""); // the real system prompt survives
    }

    [Fact]
    public async Task Tool_result_heavy_history_triggers_compaction_without_an_anchor()
    {
      var client = new FakeChatClient();
      var context = new ConversationContext();
      for (var i = 0; i < 30; i++)
      {
        context.Append(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call{i}", new string('r', 1000))]));
      }

      // 30k chars of tool results ≈ 7.5k tokens — over a 5k budget with no anchor
      // recorded (the AgentRunner path).
      Assert.True(await context.CompactIfNeededAsync(client, maxTokens: 5000));
    }
  }
  ```

- [ ] Run red: `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter 'FullyQualifiedName~ConversationContextCompactionTests'`

- [ ] Add to `src/toimi.core/ConversationContext.cs` — the four consts, `CompactIfNeededAsync`, and `MessageAsText` (algorithm carried over from `ContextManager`; the walk-back prefix scan is gone — the summary is a field). Add `using System.Text.Json;` at the top. New members:

  ```csharp
  private const int RecentMessagesToKeep = 10;
  private const int MaxToolResultCharsInSummary = 500;
  private const int MaxSummaryInputChars = 300_000;
  private const string SummaryPrefix = "Summary of earlier conversation:";

  /// <summary>
  /// Compacts the transcript when the estimate reaches <paramref name="maxTokens"/>:
  /// the prior summary (if any) plus the oldest window messages are summarized via
  /// one LLM call into the Summary slot, keeping the slots, any window-leading
  /// System messages (e.g. a fenced entity payload), and the 10 most recent
  /// exchanges. Fails soft: on summarization error/timeout the transcript is
  /// untouched — an over-budget prompt the provider trims is strictly better than
  /// dropping the user's turn.
  /// </summary>
  public async Task<bool> CompactIfNeededAsync(IChatClient client, int maxTokens = 100_000, CancellationToken ct = default)
  {
    if (Estimate() < maxTokens)
    {
      return false;
    }

    // Window-leading System messages are protected, mirroring the old
    // leading-run rule for host-appended system context.
    var leadingSystem = 0;
    while (leadingSystem < _window.Count && _window[leadingSystem].Role == ChatRole.System)
    {
      leadingSystem++;
    }

    // Same trigger arithmetic as the old ContextManager. The prior summary counts
    // as summarizable content (folded into the new summary), never as protection.
    var summaryCount = _summary is null ? 0 : 1;
    var nonSystemCount = summaryCount + (_window.Count - leadingSystem);
    if (nonSystemCount <= RecentMessagesToKeep)
    {
      return false;
    }

    var summarizeCount = nonSystemCount - RecentMessagesToKeep;
    if (summarizeCount < 2)
    {
      return false;
    }

    var fromWindow = summarizeCount - summaryCount;
    var toSummarize = new List<ChatMessage>();
    if (_summary is not null)
    {
      toSummarize.Add(_summary);
    }

    toSummarize.AddRange(_window.GetRange(leadingSystem, fromWindow));

    var conversationText = string.Join("\n\n", toSummarize.Select(MessageAsText));
    if (conversationText.Length > MaxSummaryInputChars)
    {
      conversationText = conversationText[..MaxSummaryInputChars] + "\n\n[remainder truncated]";
    }

    var summaryMessages = new List<ChatMessage>
    {
      new(ChatRole.System, "Summarize the following conversation concisely. Preserve key facts, decisions, user preferences, action items, and the outcomes of tool calls. Be brief but complete."),
      new(ChatRole.User, conversationText)
    };

    string summary;
    try
    {
      using var summaryCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
      summaryCts.CancelAfter(TimeSpan.FromSeconds(30));
      var response = await client.GetResponseAsync(summaryMessages, cancellationToken: summaryCts.Token);
      summary = response.Text ?? "Earlier conversation summary unavailable.";
    }
    catch (Exception)
    {
      // Summarization failed/timed out: proceed uncompacted.
      return false;
    }

    _window.RemoveRange(leadingSystem, fromWindow);
    _summary = new ChatMessage(ChatRole.System, $"{SummaryPrefix}\n{summary}");
    _budget.Reset();

    return true;
  }

  private static string MessageAsText(ChatMessage m)
  {
    var parts = new List<string>();
    foreach (var content in m.Contents)
    {
      switch (content)
      {
        case TextContent t when !string.IsNullOrEmpty(t.Text):
          parts.Add(t.Text);
          break;
        case FunctionCallContent fc:
          parts.Add($"[tool call: {fc.Name}({JsonSerializer.Serialize(fc.Arguments)})]");
          break;
        case FunctionResultContent fr:
          var result = fr.Result?.ToString() ?? "";
          if (result.Length > MaxToolResultCharsInSummary)
          {
            result = result[..MaxToolResultCharsInSummary] + "…";
          }

          parts.Add($"[tool result: {result}]");
          break;
        default:
          break;
      }
    }

    return $"{m.Role}: {string.Join("\n", parts)}";
  }
  ```

- [ ] Run green: `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj --filter 'FullyQualifiedName~ConversationContextCompactionTests'` — 8 passing.
- [ ] Full core suite: `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj` — expect **102 passing** (94 + 8; `ContextManagerTests`' 6 remaining facts still pass — the old code is untouched until Task 3).
- [ ] Format both core projects (apply + `--verify-no-changes`, as in Task 1).
- [ ] Commit: `git add -A && git commit -m "refactor(core): absorb compaction into ConversationContext (summary slot, no prefix scan)"` (append the Co-Authored-By trailer).

---

### Task 3: Rewire `ToimiAgent`; delete `ContextManager` and the factory message builders

Behavior-preserving rewire: **the compile break plus the existing suites are the red/green harness.** `ToimiAgentTests` and `ToimiHubTests` must pass **byte-for-byte unmodified** — that is the proof the snapshot `Messages`, folded anchoring, and absorbed compaction are behaviorally identical. Folded C2 items 1 (core half) and 3 land here because they touch the same methods.

**Files**
- Modify: `src/toimi.core/ToimiAgent.cs`
- Modify: `src/toimi.core/ToimiClientFactory.cs` (shrinks to `CreateRequestOptions` only)
- Modify: `src/toimi.core/ConversationContext.cs` (the `SystemPrompt` const moves in; `internal` reference removed)
- Modify: `src/toimi.core/Configuration/ToimiOptions.cs` (comment only)
- Delete: `src/toimi.core/ContextManager.cs`
- Delete: `src/toimi.core.Tests/ContextManagerTests.cs` (6 remaining facts — all rewritten in Task 2, see Design Decisions)
- Delete: `src/toimi.core.Tests/ToimiClientFactoryTests.cs` (see Design Decisions: the silent-no-op pin is deleted deliberately; its inverse is `Refresh_cannot_silently_degrade_whatever_the_transcript_shape`)
- Modify (mechanical retarget): `src/toimi.web.Tests/InitialMessagesTests.cs`
- Test (unmodified harnesses): `src/toimi.core.Tests/ToimiAgentTests.cs`, `src/toimi.web.Tests/ToimiHubTests.cs`

**Interfaces**
- Consumes: `ConversationContext` (Tasks 1–2).
- Changes (internal only): `ToimiAgent` private ctor takes `ConversationContext` instead of `List<ChatMessage>` + `ContextBudget`; `ToimiClientFactory` loses `CreateInitialMessages`/`RefreshDynamicContext`/`SystemPrompt`.
- Frozen: every public `ToimiAgent` member signature, `IAgentRunner`, `AgentRunner` (untouched — its `AppendMessage(ChatRole.System, ...)` lands in the window and is compaction-protected by the leading-run rule, per the Task 2 test).

**Steps**

- [ ] In `src/toimi.core/ToimiAgent.cs`:
  - Replace the fields `private readonly List<ChatMessage> _messages;`, `private readonly ContextBudget _budget;`, and `private bool _turnInProgress;` with:

    ```csharp
    private readonly ConversationContext _context;
    private int _turnState; // 0 = idle, 1 = turn in progress (CAS-guarded)
    ```

  - `Messages` property becomes a snapshot (expression-bodied property is repo-conformant):

    ```csharp
    public IReadOnlyList<ChatMessage> Messages => _context.ToChatMessages();
    ```

  - Private ctor: replace the `List<ChatMessage> messages` and `ContextBudget budget` parameters with `ConversationContext context`; assign `_context = context;` (drop `_messages`/`_budget` assignments).
  - `StartAsync`: replace

    ```csharp
    var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);
    return new ToimiAgent(config, aggregator, llm, options, messages, skillSummary, typeCatalog, budget ?? new ContextBudget(), tools.Count);
    ```

    with

    ```csharp
    var context = new ConversationContext(skillSummary, typeCatalog, budget ?? new ContextBudget());
    return new ToimiAgent(config, aggregator, llm, options, context, skillSummary, typeCatalog, tools.Count);
    ```

  - `AppendMessage` body: `_context.Append(role, text);`
  - Replace `SendAsyncCore` in full (folded item 3 — CAS guard; anchoring folded into the context):

    ```csharp
    private async IAsyncEnumerable<TurnUpdate> SendAsyncCore(string userText, [EnumeratorCancellation] CancellationToken ct = default)
    {
      // Belt-and-braces against two DIFFERENT SendAsync calls being enumerated
      // concurrently on the same agent — SingleUseTurn only guards re-enumeration
      // of one call's own sequence. CAS instead of a plain bool so two racing
      // enumerations cannot both slip past the check.
      if (Interlocked.CompareExchange(ref _turnState, 1, 0) != 0)
      {
        throw new InvalidOperationException("A turn is already in progress; SendAsync must be enumerated exactly once.");
      }

      try
      {
        _context.AppendUser(userText);
        _context.RefreshDynamicContext();

        // A summarization failure degrades gracefully inside CompactIfNeededAsync;
        // anything it does throw propagates to the host with the transcript
        // unchanged past the user message.
        await _context.CompactIfNeededAsync(_client, _config.MaxContextTokens, ct);

        var fullResponse = new StringBuilder();
        var toolEvents = new List<object>();
        UsageDetails? usage = null;

        await foreach (var update in _client.GetStreamingResponseAsync(_context.ToChatMessages(), _options, ct))
        {
          foreach (var toolUpdate in DrainToolEvents(toolEvents))
          {
            yield return toolUpdate;
          }

          foreach (var content in update.Contents)
          {
            if (content is TextContent textContent)
            {
              fullResponse.Append(textContent.Text);
              yield return new TokenUpdate(textContent.Text);
            }

            if (content is UsageContent usageContent)
            {
              usage = usageContent.Details;
            }
          }
        }

        // Drain any remaining events after streaming completes.
        foreach (var toolUpdate in DrainToolEvents(toolEvents))
        {
          yield return toolUpdate;
        }

        var responseText = fullResponse.ToString();

        // AppendAssistant anchors the budget to the prompt tokens of the transcript
        // AS SENT before appending the response — the ordering the old code
        // enforced by comment now lives inside ConversationContext.
        _context.AppendAssistant(responseText, (int?)usage?.InputTokenCount);

        // Prefer real usage from the final streaming update; fall back to the same
        // rough estimates the web host has always persisted.
        var promptTokens = (int?)usage?.InputTokenCount ?? (ContextBudget.TotalChars(_context.ToChatMessages()) / 4);
        var completionTokens = (int?)usage?.OutputTokenCount ?? (responseText.Length / 4);
        var totalTokens = (int?)usage?.TotalTokenCount ?? (promptTokens + completionTokens);

        yield return new TurnCompleted(responseText, ToolEventJson.Serialize(toolEvents), promptTokens, completionTokens, totalTokens);
      }
      finally
      {
        Volatile.Write(ref _turnState, 0);
      }
    }
    ```

  - `RunTurnAsync` (folded item 1, core half): replace the trailing

    ```csharp
    // SendAsync either throws or terminates with TurnCompleted.
    return completed!;
    ```

    with

    ```csharp
    // SendAsync's contract: it either throws or terminates with TurnCompleted.
    return completed ?? throw new InvalidOperationException("turn ended without completing");
    ```

  - `DiscardLastAssistantMessage` body: `_context.DiscardLastAssistantMessage();` (agent's method stays `void`; the doc comment's "safe no-op" contract is unchanged).
  - `Reset` body: `_context.Reset();` (the context rebuilds the dynamic message from its cached catalogs and clears the budget anchor — same net effect as the old `CreateInitialMessages` + `budget.Reset()`).
- [ ] In `src/toimi.core/ConversationContext.cs`: move the `SystemPrompt` const in — cut the entire `internal const string SystemPrompt = """ ... """;` declaration (with its `// Stable identity and behavior policies. Rarely changes.` comment) verbatim from `ToimiClientFactory.cs` (the raw string starting `You are Toimi, a personal AI assistant for a single user.` and ending `...refuse or de-escalate and offer safer alternatives.`), paste it as a `private const string SystemPrompt` among `ConversationContext`'s consts, and change the ctor's `ToimiClientFactory.SystemPrompt` reference to `SystemPrompt`.
- [ ] In `src/toimi.core/ToimiClientFactory.cs`: delete `CreateInitialMessages` and `RefreshDynamicContext` (and the now-unused `using Microsoft.Extensions.AI;` only if `CreateRequestOptions`' `AITool`/`ChatMessage` types no longer need it — they do need it, so keep; let `dotnet format` flag IDE0005). The file keeps only `CreateRequestOptions`.
- [ ] Delete `src/toimi.core/ContextManager.cs` and `src/toimi.core.Tests/ContextManagerTests.cs`:
  `git rm src/toimi.core/ContextManager.cs src/toimi.core.Tests/ContextManagerTests.cs`
- [ ] Delete `src/toimi.core.Tests/ToimiClientFactoryTests.cs`:
  `git rm src/toimi.core.Tests/ToimiClientFactoryTests.cs`
- [ ] In `src/toimi.core/Configuration/ToimiOptions.cs`, update the `MaxContextTokens` doc comment: `/// <summary>Context-window budget used by ConversationContext compaction before summarizing older messages.</summary>`
- [ ] Retarget `src/toimi.web.Tests/InitialMessagesTests.cs` mechanically — same three facts, same assertions, new construction (full file):

  ```csharp
  using Toimi.Core;
  using Xunit;

  namespace Toimi.Web.Tests;

  public class InitialMessagesTests
  {
    [Fact]
    public void Includes_type_catalog_when_provided()
    {
      var messages = new ConversationContext(skillSummary: null, typeCatalog: /*lang=json,strict*/ """[{"name":"memory"}]""").ToChatMessages();
      var context = string.Join("\n", messages.Select(m => m.Text));
      Assert.Contains("Available data types", context);
      Assert.Contains("memory", context);
    }

    [Fact]
    public void Omits_type_catalog_when_absent()
    {
      var messages = new ConversationContext().ToChatMessages();
      var context = string.Join("\n", messages.Select(m => m.Text));
      Assert.DoesNotContain("Available data types", context);
    }

    [Fact]
    public void Includes_both_skills_and_type_catalog_when_both_provided()
    {
      var messages = new ConversationContext(skillSummary: /*lang=json,strict*/ """[{"name":"daily-briefing"}]""", typeCatalog: /*lang=json,strict*/ """[{"name":"memory"}]""").ToChatMessages();
      var context = string.Join("\n", messages.Select(m => m.Text));
      Assert.Contains("Available skills", context);
      Assert.Contains("daily-briefing", context);
      Assert.Contains("Available data types", context);
      Assert.Contains("memory", context);
    }
  }
  ```

- [ ] Build everything: `dotnet build toimi.sln` — zero references to `ContextManager`/`CreateInitialMessages`/`RefreshDynamicContext` may remain. Verify: `grep -rn "ContextManager\|CreateInitialMessages\|RefreshDynamicContext(messages" src/ --include='*.cs'` returns only `ConversationContext`-internal matches (none expected for the deleted names except comments you may keep updated).
- [ ] Full harness — all three suites, with `ToimiAgentTests`, `ToimiHubTests` **unmodified** (confirm with `git status -- src/toimi.core.Tests/ToimiAgentTests.cs src/toimi.web.Tests/ToimiHubTests.cs` showing no changes):
  - `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj` — expect **93** (102 − 6 `ContextManagerTests` − 3 `ToimiClientFactoryTests`).
  - `dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj` — expect **38**.
  - `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` — expect **312** (passed+skipped).
- [ ] Format all four touched projects (core, core.Tests, web.Tests — and web builds against core, so run its format too if `dotnet format` reports anything): apply + `--verify-no-changes` each.
- [ ] Commit: `git add -A && git commit -m "refactor(core): ToimiAgent holds ConversationContext; delete ContextManager and factory message builders"` (append the Co-Authored-By trailer).

---

### Task 4: `ToimiHub` hardening — terminal-turn throw and dispose-on-leak (deferred C2 items 1 and 2)

No new tests (justified in Design Decisions: the leak fix is unobservable through the hub's seams without changing production surface; the `?? throw` replaces a null-forgiveness on a contract-unreachable path). **The unmodified 38-test web suite is the regression harness** — run it before and after; zero diffs to any test file.

**Files**
- Modify: `src/toimi.web/Hubs/ToimiHub.cs`
- Test (unmodified harness): `src/toimi.web.Tests/ToimiHubTests.cs`

**Interfaces**
- Consumes: existing `ToimiAgent.StartAsync`/`DisposeAsync`. No signature changes anywhere; SignalR event surface unchanged.

**Steps**

- [ ] In `SendMessage`, replace

  ```csharp
  // SendAsync either throws or terminates with TurnCompleted.
  var turn = completed!;
  ```

  with

  ```csharp
  // SendAsync's contract: it either throws or terminates with TurnCompleted.
  var turn = completed ?? throw new InvalidOperationException("turn ended without completing");
  ```

  (The surrounding `catch (Exception ex)` already converts this to an `Error` event — previously it was a latent NRE.)

- [ ] Replace `OnConnectedAsync` in full (only the agent-lifetime scaffolding changes; the conversation-loading body is verbatim from today):

  ```csharp
  public override async Task OnConnectedAsync()
  {
    ToimiAgent? agent = null;
    var registered = false;
    try
    {
      agent = await ToimiAgent.StartAsync(_config, _llmProvider, logger: logger, ct: Context.ConnectionAborted);

      // Check for conversationId query parameter
      var conversationIdParam = Context.GetHttpContext()?.Request.Query["conversationId"].ToString();

      // Lazy conversations: no DB row is written on connect. Only an existing,
      // query-param-named conversation resolves to an id here; a no-param connect
      // (or an unknown/deleted id) starts with a null ConversationId and no row.
      // The row is created on the first message (see SendMessage), which then emits
      // ConversationCreated so the client can learn its id for reconnect-resync.
      Guid? conversationId = null;

      if (!string.IsNullOrEmpty(conversationIdParam) && Guid.TryParse(conversationIdParam, out var existingId))
      {
        var conversation = await _repository.GetByIdAsync(existingId);
        if (conversation is not null)
        {
          conversationId = conversation.Id;

          // Replay stored messages into the agent's transcript
          foreach (var msg in conversation.Messages)
          {
            agent.AppendMessage(msg.Role == "user" ? ChatRole.User : ChatRole.Assistant, msg.Content);
          }

          // Send ConversationLoaded with messages
          var messagesJson = SerializeConversationMessages(conversation.Messages);
          await Clients.Caller.SendAsync("ConversationLoaded", conversation.Id, messagesJson);
        }
        // else: unknown/deleted id — fall through as a fresh, lazy conversation.
        // No ConversationLoaded is sent; the client keeps its empty view and learns
        // a real id from ConversationCreated once the first message creates the row.
      }
      // No-param connect is lazy too: no send, no row. The client's fresh view
      // (empty messages, no id) already reflects this state.

      Sessions[Context.ConnectionId] = new ToimiSession(agent, conversationId);
      registered = true;

      await Clients.Caller.SendAsync("Connected", agent.ToolCount);
    }
    catch (Exception ex)
    {
      // A started-but-unregistered agent would leak its MCP connections: without a
      // Sessions entry, OnDisconnectedAsync will never dispose it. Best-effort
      // dispose here; once registered, disconnect owns disposal.
      if (agent is not null && !registered)
      {
        try
        {
          await agent.DisposeAsync();
        }
        catch
        {
          // Disposal is best-effort on the failure path.
        }
      }

      await Clients.Caller.SendAsync("Error", $"Failed to initialize: {ex.Message}");
      Context.Abort();
      return;
    }

    await base.OnConnectedAsync();
  }
  ```

- [ ] Verify no test file changed: `git status --porcelain src/toimi.web.Tests/` must be empty.
- [ ] Run the harness: `dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj` — **38 passing, unmodified**.
- [ ] Format: `dotnet format src/toimi.web/toimi.web.csproj && dotnet format src/toimi.web/toimi.web.csproj --verify-no-changes`
- [ ] Commit: `git add -A && git commit -m "fix(web): dispose unregistered agent on connect failure; explicit turn-completion check"` (append the Co-Authored-By trailer).

---

### Task 5: Full gate + CLAUDE.md wording

**Files**
- Modify: `CLAUDE.md`
- Test: all three suites (gate)

**Interfaces**
- None — documentation and verification only.

**Steps**

- [ ] Full gate, in order, all green:

  ```bash
  export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"
  dotnet build toimi.sln
  dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj            # ≥ 93
  dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj              # 38
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj  # 312 passed+skipped
  ```

- [ ] Verify formatting across every project touched in this plan:

  ```bash
  for p in toimi.core toimi.core.Tests toimi.web toimi.web.Tests; do
    dotnet format "src/$p/$p.csproj" --verify-no-changes || exit 1
  done
  ```

- [ ] Update `CLAUDE.md` — two edits:
  1. The **toimi.core** bullet (currently "…conversation persistence (`ToimiDbContext`), context-window management (`ContextManager`), system-prompt assembly + catalog injection (`ToimiClientFactory`).") becomes:

     ```
     conversation persistence (`ToimiDbContext`), the transcript + context-window
     management (`ConversationContext`: owns the system-prompt/dynamic-context/
     summary slots, catalog injection, compaction, and `ContextBudget` anchoring),
     request-option assembly (`ToimiClientFactory`).
     ```

  2. The **Key Patterns → Context window management** bullet (currently "…`ContextManager` in core estimates token count before each LLM call. Near the ~100k limit it summarizes older messages…") becomes:

     ```
     - **Context window management** — `ConversationContext` in core owns the
       transcript as slots (system prompt, refreshable dynamic context, optional
       compaction summary, exchange window) and estimates token count before each
       LLM call. Near the ~100k limit it summarizes older messages via the LLM
       into the summary slot, preserving system messages and the 10 most recent
       exchanges.
     ```

- [ ] Commit: `git add CLAUDE.md && git commit -m "docs: ConversationContext wording in CLAUDE.md"` (append the Co-Authored-By trailer).
