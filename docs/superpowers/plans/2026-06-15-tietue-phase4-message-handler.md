# Tietue Phase 4 — Message Handler, Activate & Schedule Seeding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a trigger fire a full **LLM agent session** via a `message` native handler — running a prompt (rendered from the entity's `Data`) against the whole MCP tool surface, recording the run in `entity_events`. Add an **`activate`** MCP verb (run an entity's agent now, off-cycle, or schedule it), and seed a **`schedule`** standard type. Because the agent can call tietue's own `set_trigger` over MCP, entities can **self-schedule**. This functionally replaces `ajastin` (autonomous scheduled agent runs); the ajastin pod retires at the Phase 6 cutover.

**Architecture:** A reusable `AgentRunner` (`IAgentRunner`) runs an **ephemeral** headless agent turn exactly the way ajastin's `ScheduleWorker.ExecutePrompt` does — `McpToolAggregator.ConnectAllAsync(config.McpServers)` → `ToimiClientFactory.Create/CreateRequestOptions/CreateInitialMessages` → inject the entity's `Data` as context + the prompt → `client.GetResponseAsync` (the `UseFunctionInvocation` tool loop) → drain `ToolCallNotifier` → return `(success, response, toolCalls, error)`. A `MessageHandler : INativeHandler` (kind `"message"`) renders its `promptTemplate` from `Data` and delegates to `AgentRunner`; the existing scheduler records the result as an `entity_events` row (`kind="message"`). `activate(entityId, message, when?)` runs the agent now (via `AgentRunner`) or schedules a `message` trigger. The seeded `schedule` type has a default `message` trigger driven by its `prompt`/`startAt`/`rrule` fields. `AgentRunner` (LLM+MCP I/O) is not unit-tested (like ajastin's worker); `MessageHandler` and `ActivateTool` are tested against a fake `IAgentRunner`.

**Tech Stack:** .NET 10, the existing `toimi.core` agent stack (`Microsoft.Extensions.AI` 10.3.0, `Microsoft.Extensions.AI.OpenAI` 10.3.0, `ModelContextProtocol` 1.1.0), EF Core 10 + Npgsql, xUnit + EF InMemory. Run dotnet inside the cached .NET 10 SDK Docker image (`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`). The repo enforces `dotnet format` (IDE0005 unused usings, IDE0022 block bodies, IDE0046 'if can be simplified', whitespace) as **errors** — before each commit run `dotnet format` apply on both csproj, then `--verify-no-changes` and confirm each exits 0 (capture `$?`, don't pipe to `tail`); hand-fix IDE0046 if the apply step leaves it; commit with `git add -A src/toimi.tools.tietue src/toimi.tools.tietue.Tests`.

**Scope boundary (Phase 4 of the §16 build order):**
- IN: `IAgentRunner`/`AgentRunner` (ephemeral), `MessageHandler` (kind `message`), the `activate` MCP verb (run-now + schedule), `schedule` standard-type seeding, the toimi.core agent-stack wiring in tietue (`ToimiConfiguration` + `McpServers` + DI), deployment env (OpenAI key/model).
- OUT / DEFERRED (noted inline): **lazy threaded per-entity conversations** (`Conversation.EntityId` + persistence) — requires a cross-project `toimi.core` schema change + tietue connecting to the shared `toimi` DB, is the design's explicit *exception* (ephemeral is the default), and is NOT needed for ajastin parity (ajastin runs are stateless). The **script sandbox** (Phase 5). Deleting the ajastin pod/DB (Phase 6 cutover). `cron` expressions (the `schedule` type uses RFC 5545 `rrule` — tietue's unified recurrence — not Cronos `cron`; the AI expresses recurrence as an `rrule`).

**Assumes Phases 1–3 are merged** (entities, types with behaviors + default triggers, semantic index, triggers + scheduler + `notify`/`set-field` handlers + `entity_events` + reminder seeding).

---

## File Structure

**New in `src/toimi.tools.tietue/`:**
- `Agents/IAgentRunner.cs` — interface + `AgentRunResult` record
- `Agents/AgentRunner.cs` — real ephemeral runner (toimi.core agent stack; not unit-tested)
- `Handlers/MessageHandler.cs` — `INativeHandler` kind `"message"`
- `Tools/ActivateTool.cs` — `activate` MCP verb
- `Seed/` — `schedule` added to `TypeSeeder`

**New in `src/toimi.tools.tietue.Tests/`:**
- `FakeAgentRunner.cs` — captures the (entity, prompt) it was asked to run
- `MessageHandlerTests.cs`, `ActivateToolTests.cs`, plus a `TypeSeederTests` extension

**Modified:**
- `toimi.tools.tietue/appsettings.json` — `Toimi` section (OpenAI + McpServers)
- `toimi.tools.tietue/Program.cs` — register `ToimiConfiguration`, `IAgentRunner`→`AgentRunner`, `MessageHandler` as an `INativeHandler`
- `toimi.tools.tietue/Seed/TypeSeeder.cs` — seed `schedule`
- `k8s/base/tools-tietue/deployment.yaml` — `Toimi__OpenAI__ApiKey` + `Toimi__OpenAI__Model` env

---

## Task 1: `IAgentRunner` + `AgentRunResult` + `FakeAgentRunner`

**Files:**
- Create: `src/toimi.tools.tietue/Agents/IAgentRunner.cs`
- Create: `src/toimi.tools.tietue.Tests/FakeAgentRunner.cs`

- [ ] **Step 1: the interface + result.** `src/toimi.tools.tietue/Agents/IAgentRunner.cs`:
```csharp
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Agents;

public record AgentRunResult(bool Success, string Response, string? ToolCallsJson, string? Error);

public interface IAgentRunner
{
  // Runs one ephemeral agent turn for `prompt`, with the entity's Data injected as context.
  Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default);
}
```

- [ ] **Step 2: the test fake.** `src/toimi.tools.tietue.Tests/FakeAgentRunner.cs`:
```csharp
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Tests;

public class FakeAgentRunner : IAgentRunner
{
  public List<(Entity Entity, string Prompt)> Runs { get; } = [];
  public AgentRunResult Result { get; set; } = new(true, "ok", null, null);

  public Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default)
  {
    Runs.Add((entity, prompt));
    return Task.FromResult(Result);
  }
}
```

- [ ] **Step 3: build the test project; confirm it compiles.**
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`

- [ ] **Step 4: LINT (verify main + tests exit 0) + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj; dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo MAIN=$?; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo TESTS=$?'
git add -A src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): add IAgentRunner abstraction and test fake"
```

## Your Job (every task)
Implement exactly as specified, TDD where tests are given. Before committing: `dotnet format` apply both projects, then `--verify-no-changes` exit 0 on BOTH (hand-fix IDE0046 if the apply leaves it — rewrite the flagged `if`-return into a conditional expression). `git add -A` the tietue dirs; confirm clean tree. Report Status, test/suite counts, MAIN/TESTS verify exit codes, commit SHA, concerns.

---

## Task 2: `MessageHandler`

**Files:**
- Create: `src/toimi.tools.tietue/Handlers/MessageHandler.cs`
- Test: `src/toimi.tools.tietue.Tests/MessageHandlerTests.cs`

`MessageHandler` (ns `toimi.tools.tietue.Handlers`, kind `"message"`) reads its config `{ "promptTemplate": "..." }`, renders the template from the entity's `Data` via the existing `TemplateRenderer.Render(string?, JsonDocument)`, calls `IAgentRunner.RunAsync(entity, prompt, ct)`, and returns a `HandlerResult` whose `Status` is `"ran"` (success) or `"error"`, and whose `Result` is a JSON blob `{response, success, error}`. (`HandlerContext(Entity Entity, string? ConfigJson, DateTimeOffset OccurrenceUtc)`, `HandlerResult(string Status, string? Result = null)` already exist.)

- [ ] **Step 1: failing tests.** `src/toimi.tools.tietue.Tests/MessageHandlerTests.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class MessageHandlerTests
{
  private static Entity Schedule(string prompt) => new()
  {
    Id = Guid.NewGuid(),
    Type = "schedule",
    Data = JsonDocument.Parse($$"""{"name":"daily","prompt":"{{prompt}}"}"""),
    CreatedAt = DateTimeOffset.UtcNow,
    UpdatedAt = DateTimeOffset.UtcNow,
  };

  [Fact]
  public async Task Renders_prompt_from_data_and_runs_agent()
  {
    var runner = new FakeAgentRunner();
    var handler = new MessageHandler(runner);
    var e = Schedule("Give me a morning briefing");

    var result = await handler.HandleAsync(new HandlerContext(e, """{"promptTemplate":"{prompt}"}""", DateTimeOffset.UtcNow));

    var run = Assert.Single(runner.Runs);
    Assert.Equal("Give me a morning briefing", run.Prompt);
    Assert.Same(e, run.Entity);
    Assert.Equal("ran", result.Status);
  }

  [Fact]
  public async Task Reports_error_status_when_run_fails()
  {
    var runner = new FakeAgentRunner { Result = new(false, "", null, "boom") };
    var handler = new MessageHandler(runner);

    var result = await handler.HandleAsync(new HandlerContext(Schedule("x"), """{"promptTemplate":"{prompt}"}""", DateTimeOffset.UtcNow));

    Assert.Equal("error", result.Status);
    Assert.Contains("boom", result.Result);
  }
}
```

- [ ] **Step 2: run, confirm FAIL.**

- [ ] **Step 3: implement.** `src/toimi.tools.tietue/Handlers/MessageHandler.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Agents;

namespace toimi.tools.tietue.Handlers;

public class MessageHandler(IAgentRunner runner) : INativeHandler
{
  public string Kind => "message";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    string? promptTemplate = null;
    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      if (cfg.RootElement.TryGetProperty("promptTemplate", out var p) && p.ValueKind == JsonValueKind.String)
      {
        promptTemplate = p.GetString();
      }
    }

    var prompt = TemplateRenderer.Render(promptTemplate, ctx.Entity.Data);
    var run = await runner.RunAsync(ctx.Entity, prompt, ct);
    var result = JsonSerializer.Serialize(new { run.Response, run.Success, run.Error });
    return new HandlerResult(run.Success ? "ran" : "error", result);
  }
}
```

- [ ] **Step 4: run, confirm 2 PASS + full suite green. LINT verify exit 0. Commit** (`git add -A` tietue dirs):
```
git commit -m "feat(tietue): add message handler that runs an agent session"
```

---

## Task 3: `AgentRunner` (real, ephemeral) + `ToimiConfiguration` wiring

**Files:**
- Create: `src/toimi.tools.tietue/Agents/AgentRunner.cs`
- Modify: `src/toimi.tools.tietue/appsettings.json`

> Not unit-tested (LLM + MCP I/O, exactly like ajastin's `ScheduleWorker.ExecutePrompt`). Mirror that method precisely. Open `src/toimi.tools.ajastin/Worker/ScheduleWorker.cs` (the `ExecutePrompt` method) to copy the exact call sequence, and `src/toimi.core/ToimiClientFactory.cs` + `McpToolAggregator.cs` + `ContextManager.cs` for signatures.

- [ ] **Step 1: add `Toimi` config to `src/toimi.tools.tietue/appsettings.json`** (mirror ajastin's appsettings; the `McpServers` list should include all tool servers AND tietue itself so an agent run can call tietue's own tools for self-scheduling). Add this top-level section:
```json
  "Toimi": {
    "OpenAI": { "ApiKey": "", "Model": "gpt-4o" },
    "McpServers": [
      { "Name": "koti", "Transport": "Http", "Url": "http://toimi-tools-koti.apps.svc.cluster.local/sse" },
      { "Name": "muistio", "Transport": "Http", "Url": "http://toimi-tools-muistio.apps.svc.cluster.local/sse" },
      { "Name": "muistutin", "Transport": "Http", "Url": "http://toimi-tools-muistutin.apps.svc.cluster.local/sse" },
      { "Name": "taidot", "Transport": "Http", "Url": "http://toimi-tools-taidot.apps.svc.cluster.local/sse" },
      { "Name": "verkko", "Transport": "Http", "Url": "http://toimi-tools-verkko.apps.svc.cluster.local/sse" },
      { "Name": "ruutu", "Transport": "Http", "Url": "http://toimi-tools-ruutu.apps.svc.cluster.local/sse" },
      { "Name": "tietue", "Transport": "Http", "Url": "http://toimi-tools-tietue.apps.svc.cluster.local/sse" }
    ]
  }
```
(Keep the existing `Ntfy`, `Qdrant`, `OpenAI`, `ConnectionStrings` sections intact — this `Toimi` section is additive. Note: tietue keeps its own top-level `OpenAI` section for embeddings (Phase 2); this new `Toimi:OpenAI` is for the agent LLM, mirroring ajastin.)

- [ ] **Step 2: implement `AgentRunner`.** `src/toimi.tools.tietue/Agents/AgentRunner.cs` — mirror ajastin's `ExecutePrompt`, but inject the entity as context before the prompt:
```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
using Toimi.Core;
using Toimi.Core.Configuration;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Agents;

public class AgentRunner(ToimiConfiguration config) : IAgentRunner
{
  public async Task<AgentRunResult> RunAsync(Entity entity, string prompt, CancellationToken ct = default)
  {
    try
    {
      await using var aggregator = new McpToolAggregator();
      await aggregator.ConnectAllAsync(config.McpServers, ct);
      var tools = aggregator.GetAllTools();

      var skillSummary = await aggregator.CallToolAsync("list_skills", ct: ct);
      var typeCatalog = await aggregator.CallToolAsync("list_types", ct: ct);

      var (client, notifier) = ToimiClientFactory.Create(config);
      var options = ToimiClientFactory.CreateRequestOptions(tools);
      var messages = ToimiClientFactory.CreateInitialMessages(skillSummary, typeCatalog);

      messages.Add(new(ChatRole.System,
        $"You are acting on a '{entity.Type}' entity (id {entity.Id}). Its current data is:\n{entity.Data.RootElement.GetRawText()}\n" +
        "Use the tietue tools (create/update/search/set_trigger/...) to act on it; you may schedule your own next run with set_trigger on this entity id."));
      messages.Add(new(ChatRole.User, prompt));

      ToimiClientFactory.RefreshDynamicContext(messages);
      await ContextManager.CompactIfNeeded(messages, client, ct);

      var response = await client.GetResponseAsync(messages, options, ct);
      var responseText = response.Text ?? "";

      var toolCalls = new List<object>();
      while (notifier.TryDequeueEvent(out var evt))
      {
        toolCalls.Add(evt!);
      }

      var toolCallsJson = toolCalls.Count > 0 ? JsonSerializer.Serialize(toolCalls) : null;
      return new AgentRunResult(true, responseText, toolCallsJson, null);
    }
    catch (Exception ex)
    {
      return new AgentRunResult(false, "", null, ex.Message);
    }
  }
}
```
> Verify against ajastin: the exact names `ToimiClientFactory.Create(config)` returning `(IChatClient, ToolCallNotifier)`, `CreateRequestOptions(tools)`, `CreateInitialMessages(skillSummary, typeCatalog)`, `RefreshDynamicContext(messages)`, `ContextManager.CompactIfNeeded(messages, client, ct)`, `notifier.TryDequeueEvent(out var evt)`, and `aggregator.GetAllTools()`/`ConnectAllAsync`/`CallToolAsync`. Adjust any signature/namespace that differs from the real toimi.core code (e.g. the exact namespace of `ToimiConfiguration` is `Toimi.Core.Configuration`; confirm). `McpToolAggregator` and `ToimiClientFactory` are in namespace `Toimi.Core`.

- [ ] **Step 3: build the main project; confirm it compiles** (this is the integration-surface check):
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj`
Expected: `Build succeeded.` If any toimi.core signature differs, align with ajastin's working usage and report the adjustment.

- [ ] **Step 4: validate appsettings JSON, LINT verify exit 0, commit** (`git add -A` tietue dir):
```
git commit -m "feat(tietue): add ephemeral agent runner and Toimi agent config"
```

---

## Task 4: Program wiring — register agent config, runner, message handler

**Files:**
- Modify: `src/toimi.tools.tietue/Program.cs`

- [ ] **Step 1: register the agent stack.** In `src/toimi.tools.tietue/Program.cs`, after the existing handler/scheduler registrations and before `AddMcpServer(...)`, add:
```csharp
builder.Services.AddSingleton(
  builder.Configuration.GetSection("Toimi").Get<Toimi.Core.Configuration.ToimiConfiguration>()
    ?? throw new InvalidOperationException("Toimi configuration is required"));
builder.Services.AddSingleton<toimi.tools.tietue.Agents.IAgentRunner, toimi.tools.tietue.Agents.AgentRunner>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.MessageHandler>();
```
(`MessageHandler` registered scoped alongside the existing `NotifyHandler`/`SetFieldHandler` so the scoped `HandlerRegistry` resolves all three. `AgentRunner` is a singleton — it holds only config and creates a fresh `McpToolAggregator` per run.)

- [ ] **Step 2: handle the test factory.** The `AdminEndpointsTests` boot the real `Program`. Adding the required-`Toimi` config means the test host must supply it. In `src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs` `TietueTestFactory.ConfigureWebHost`, add minimal settings so the `Get<ToimiConfiguration>()` is non-null:
```csharp
      builder.UseSetting("Toimi:OpenAI:ApiKey", "test-key");
      builder.UseSetting("Toimi:OpenAI:Model", "gpt-4o");
```
(No `McpServers` needed for the tests — an empty list is fine; the agent runner is never invoked in the admin tests, and the `TriggerWorker` tick finds no `message` triggers in the empty in-memory DB.)
> If `Get<ToimiConfiguration>()` returns null when only `OpenAI` settings are present (because `ToimiConfiguration.OpenAI` is `required`), the two `UseSetting` lines above populate it. Confirm the admin tests still boot; if binding still yields null, also set a dummy `Toimi:McpServers:0:Name`/`:Transport`/`:Url` triple — but try without first.

- [ ] **Step 3: run the FULL suite; confirm all pass. LINT verify exit 0. Commit:**
```
git commit -m "feat(tietue): wire agent runner, Toimi config, and message handler"
```

---

## Task 5: `activate` MCP verb

**Files:**
- Create: `src/toimi.tools.tietue/Tools/ActivateTool.cs`
- Test: `src/toimi.tools.tietue.Tests/ActivateToolTests.cs`

`activate(entityId, message, when?)`: with `when` omitted, run the entity's agent NOW via `IAgentRunner` and record an `entity_events` row (`kind="message"`); with `when` (ISO 8601 UTC), schedule a one-shot `message` trigger whose `promptTemplate` is the literal message. Dependencies: `EntityRepository` (ns `toimi.tools.tietue.Entities`; `GetAsync(Guid) → Entity?`), `IAgentRunner`, `EntityEventStore` (ns `toimi.tools.tietue.Events`; `RecordAsync(entityId, occurrenceUtc, kind, status, result, ct)`), `TriggerRepository` (ns `toimi.tools.tietue.Scheduling`; `CreateAsync(entityId, scheduleJson, handlerKind, handlerConfig, now, ct)`).

- [ ] **Step 1: failing tests.** `src/toimi.tools.tietue.Tests/ActivateToolTests.cs`:
```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ActivateToolTests
{
  private const string Schema = """{"type":"object","properties":{"name":{"type":"string"}}}""";

  private static async Task<(EntityRepository entities, FakeAgentRunner runner, EntityEventStore events, TriggerRepository triggers, Guid entityId)> SetupAsync(toimi.tools.tietue.Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x"}"""), []);
    return (entities, new FakeAgentRunner(), new EntityEventStore(db), new TriggerRepository(db), e.Id);
  }

  [Fact]
  public async Task Activate_now_runs_agent_and_records_event()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(id.ToString(), "do the thing", null);

    var run = Assert.Single(runner.Runs);
    Assert.Equal("do the thing", run.Prompt);
    Assert.Contains("ok", result);
  }

  [Fact]
  public async Task Activate_with_when_schedules_a_message_trigger()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, id) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(id.ToString(), "later thing", "2026-07-01T09:00:00Z");

    Assert.Empty(runner.Runs);
    var t = Assert.Single(await triggers.ListByEntityAsync(id));
    Assert.Equal("message", t.HandlerKind);
    Assert.Contains("later thing", t.HandlerConfig);
  }

  [Fact]
  public async Task Activate_unknown_entity_returns_message()
  {
    using var db = TestDb.New();
    var (entities, runner, events, triggers, _) = await SetupAsync(db);
    var tool = new ActivateTool(entities, runner, events, triggers);

    var result = await tool.Activate(Guid.NewGuid().ToString(), "x", null);

    Assert.Contains("not found", result);
  }
}
```

- [ ] **Step 2: run, confirm FAIL.**

- [ ] **Step 3: implement.** `src/toimi.tools.tietue/Tools/ActivateTool.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ActivateTool(EntityRepository entities, IAgentRunner runner, EntityEventStore events, TriggerRepository triggers)
{
  [McpServerTool, Description("Activate an entity's agent: run a prompt against it now (omit 'when'), or schedule it for later ('when' = ISO 8601 UTC). The agent can act on the entity and schedule its own next run via set_trigger.")]
  public async Task<string> Activate(
      [Description("Entity id (GUID)")] string entityId,
      [Description("The prompt/message for the agent")] string message,
      [Description("Optional ISO 8601 UTC time to schedule it for; omit to run now")] string? when = null)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    if (when is not null)
    {
      if (!DateTimeOffset.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
      {
        return "Invalid 'when'. Use ISO 8601 (e.g. 2026-07-01T09:00:00Z).";
      }

      var schedule = new JsonObject { ["at"] = at.ToString("o") }.ToJsonString();
      var config = new JsonObject { ["promptTemplate"] = message }.ToJsonString();
      var t = await triggers.CreateAsync(id, schedule, "message", config, DateTimeOffset.UtcNow);
      return JsonSerializer.Serialize(new { scheduled = true, triggerId = t.Id.ToString(), at = at.ToString("o") });
    }

    var entity = await entities.GetAsync(id);
    if (entity is null)
    {
      return $"Entity '{entityId}' not found.";
    }

    var now = DateTimeOffset.UtcNow;
    var run = await runner.RunAsync(entity, message, default);
    await events.RecordAsync(id, now, "message", run.Success ? "ran" : "error",
      JsonSerializer.Serialize(new { run.Response, run.Success, run.Error }));
    return JsonSerializer.Serialize(new { ran = true, run.Success, run.Response, run.Error });
  }
}
```
> Note: the `promptTemplate` stored for a scheduled activation is the literal message (no `{tokens}`), so `TemplateRenderer.Render` returns it verbatim at fire time. The `events.RecordAsync` for run-now uses `kind="message"` (consistent with how the scheduler records `message`-trigger firings).

- [ ] **Step 4: run, confirm 3 PASS + full suite green. LINT verify exit 0. Commit:**
```
git commit -m "feat(tietue): add activate MCP verb (run-now or schedule)"
```

---

## Task 6: Seed the `schedule` standard type

**Files:**
- Modify: `src/toimi.tools.tietue/Seed/TypeSeeder.cs`
- Test: `src/toimi.tools.tietue.Tests/TypeSeederTests.cs` (extend)

- [ ] **Step 1: add the `schedule` entry** to `TypeSeeder.StandardTypes` (the 4-tuple `(Name, Schema, Behaviors, DefaultTriggers)` from Phase 3). It mirrors ajastin's Schedule (name + prompt + recurrence) but uses an RFC 5545 `rrule` (tietue's unified recurrence) instead of cron, and a default `message` trigger that runs the prompt:
```csharp
    (
      "schedule",
      """
      {"type":"object","properties":{
        "name":{"type":"string","description":"short schedule name"},
        "prompt":{"type":"string","description":"the instruction the agent runs each time"},
        "startAt":{"type":"string","description":"first run time, ISO 8601 UTC"},
        "rrule":{"type":"string","description":"optional RFC 5545 RRULE for recurrence (e.g. FREQ=DAILY)"}
      },"required":["name","prompt","startAt"]}
      """,
      null,
      """
      [{"when":{"atField":"startAt","rruleField":"rrule"},
        "handler":{"kind":"message","config":{"promptTemplate":"{prompt}"}}}]
      """
    ),
```

- [ ] **Step 2: extend the seeder test.** In `src/toimi.tools.tietue.Tests/TypeSeederTests.cs` add:
```csharp
  [Fact]
  public async Task Seeds_schedule_with_default_message_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var schedule = await repo.GetAsync("schedule");
    Assert.NotNull(schedule);
    Assert.Contains("message", schedule!.DefaultTriggers!);
    Assert.Contains("prompt", schedule.DefaultTriggers!);
  }
```
Update the idempotency / type-count assertion from 3 to **4** (memory, skill, reminder, schedule).

- [ ] **Step 3: run seeder tests + full suite green. LINT verify exit 0. Commit:**
```
git commit -m "feat(tietue): seed schedule standard type with default message trigger"
```

---

## Task 7: Deployment env — Toimi OpenAI for the agent

**Files:**
- Modify: `k8s/base/tools-tietue/deployment.yaml`

- [ ] **Step 1: add the agent env** to the container `env:` list (mirror `k8s/base/tools-ajastin/deployment.yaml`: the `openai-api-key` secret already exists; model comes from the `${OPENAI_MODEL}` placeholder rendered by the deploy script):
```yaml
            - name: Toimi__OpenAI__ApiKey
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: openai-api-key
            - name: Toimi__OpenAI__Model
              value: "${OPENAI_MODEL}"
```
(Match the existing indentation. The `McpServers` list is baked into `appsettings.json`, so no extra env is needed for it.)

- [ ] **Step 2: validate YAML; commit:**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -lc "python3 -c 'import yaml; yaml.safe_load(open(\"k8s/base/tools-tietue/deployment.yaml\")); print(\"YAML_OK\")'"
git add -A k8s/base/tools-tietue/deployment.yaml
git commit -m "feat(tietue): add Toimi OpenAI agent env to deployment"
```

---

## Task 8: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: tietue suite + lint (real exit codes).**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo "MAIN=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo "TESTS=$?"
'
```
Expected: all tests pass (Phases 1–3 plus the new Phase 4 tests), `MAIN=0`, `TESTS=0`.

- [ ] **Step 2: manual smoke (optional, recommended).** With a real Postgres + OpenAI key + the MCP servers reachable: create a `schedule` entity with `startAt` a minute out + a simple `prompt` (e.g. "send me a test notification via verkko"); confirm a `message` trigger was provisioned (`list_triggers`); wait for the worker tick; confirm the agent ran (an `entity_events` row `kind=message`, response logged) and the prompt's effect happened. Then `activate(entityId, "...")` and confirm the agent runs immediately.

- [ ] **Step 3: final commit if anything changed.**
```bash
git add -A && git commit -m "chore(tietue): phase 4 message handler complete" --allow-empty
```

---

## Phase 4 Done — what exists now

A trigger can now fire a **full agent session**: the `message` handler renders a prompt from the entity's `Data` and runs it against the entire MCP tool surface (reusing toimi.core's agent stack), logging the run to `entity_events`. The **`activate`** verb runs an entity's agent on demand or schedules it. The seeded **`schedule`** type means autonomous recurring agent runs work through `tietue` — functionally replacing `ajastin`. Because an agent run can call tietue's own `set_trigger`, entities **self-schedule**. The ajastin pod retires at the Phase 6 cutover.

**Deferred (noted inline):** lazy threaded per-entity conversations (the design's *exception*; needs a cross-project `toimi.core` `Conversation.EntityId` change + tietue↔`toimi`-DB access; not needed for ajastin parity), and `cron` (the `schedule` type uses `rrule`).

**Next phases (separate plans):**
- **Phase 5** — the sandboxed `script` handler + escalation (the long-tail custom logic escape hatch).
- **Phase 6** — cutover: delete muistio/taidot/muistutin/ajastin pods, DBs, k8s bases; update standard-skill seeds + the web/agent `McpServers` URLs (drop the retired servers, keep koti/verkko/tietue).
```
