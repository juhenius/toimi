# Tietue Phase 5 — Sandboxed Script Handler & Escalation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the escape-hatch native handler `script` — AI-authored JavaScript that runs in a sandbox when a trigger fires, for the long tail of custom per-type logic that no built-in handler covers. The script is a **pure function of the entity's `Data`** that returns a declarative **effects** object; the host applies only the effects the script's **capability grant** allows (`setField`, `notify`, `trigger`, `escalate`). `escalate` wakes a full agent run (the Phase 4 `AgentRunner`), so a cheap deterministic script can hand off to the LLM when it hits something it can't handle.

**Architecture & security stance:** The script runs in **Jint** (a pure-.NET JavaScript interpreter) with **no CLR access, no host IO**, and **timeout / statement / memory caps**. The only thing the script receives is the entity's `Data` (read-only, as a parsed JS object); the only thing it produces is an **effects JSON** object it `return`s. The host (`ScriptEffectApplier`) validates each effect against the script's declared `capabilities` allowlist and applies the allowed ones via the existing services (`EntityRepository`, `INotifier`, `TriggerRepository`, `IAgentRunner`). This "pure-function-returns-effects" design is a deliberate, safer realization of design-study §6's capability model: the sandbox is incapable of side effects by construction, effects are gated host-side, and both halves (script evaluation, effect application) are unit-testable. A global `Scripts:Enabled` kill switch and structured logging round out the guards.

**Tech Stack:** .NET 10, `Jint` (latest stable 4.x), the existing tietue services (`EntityRepository`, `INotifier`, `TriggerRepository`, `IAgentRunner`, `EntityEventStore`), `ModelContextProtocol` 1.1.0, xUnit. Run dotnet inside the cached .NET 10 SDK Docker image (`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`). The repo enforces `dotnet format` (IDE0005, IDE0022, IDE0046, whitespace) as **errors** — before each commit run `dotnet format` apply on both csproj, then `--verify-no-changes` and confirm each exits 0 (capture `$?`, don't pipe to `tail`); hand-fix IDE0046 if the apply leaves it; commit with `git add -A src/toimi.tools.tietue src/toimi.tools.tietue.Tests`.

**Scope boundary (Phase 5 of the §16 build order):**
- IN: the `script` native handler (kind `"script"`) — Jint sandbox (no CLR/IO, time/statement/memory caps), the effects schema (`setField`/`notify`/`trigger`/`escalate`), capability-grant gating, the global `Scripts:Enabled` kill switch, escalation to `AgentRunner`, structured logging, DI wiring. Scripts are authored by the agent via the existing `set_trigger`/`define_type` config (handler kind `"script"`, config `{source, capabilities}`) — **no new MCP tool needed**.
- OUT / DEFERRED (noted inline): a `fetch` capability (network egress — the most security-sensitive; pairs with the deferred `poll-diff` handler; add later behind a domain allowlist); script-as-`validate`/`derive`/`renderMessage` lifecycle hooks (design §5 — a separate broader extension; Phase 5 ships the trigger *handler* only); per-script/per-type kill switches (a global flag suffices for v1); WASM/Extism runtime (the host API is designed so the engine could be swapped later). The Phase 6 cutover (deleting old pods) is separate.

**Assumes Phases 1–4 are merged** (entities, types, semantic index, triggers + scheduler + native handlers `notify`/`set-field`/`message`, `entity_events`, copy-down, `AgentRunner`, seeded `memory`/`skill`/`reminder`/`schedule`).

---

## File Structure

**New in `src/toimi.tools.tietue/`:**
- `Scripts/ScriptEngine.cs` — runs a JS script with guards; returns the effects JSON string
- `Scripts/ScriptEffects.cs` — parse effects JSON → typed `ScriptEffects`
- `Scripts/ScriptEffectApplier.cs` — apply allowed effects via existing services
- `Scripts/ScriptOptions.cs` — bound config (`Enabled`, caps)
- `Handlers/ScriptHandler.cs` — `INativeHandler` kind `"script"`

**New in `src/toimi.tools.tietue.Tests/`:**
- `ScriptEngineTests.cs`, `ScriptEffectsTests.cs`, `ScriptEffectApplierTests.cs`, `ScriptHandlerTests.cs`

**Modified:**
- `toimi.tools.tietue.csproj` — add `Jint`
- `appsettings.json` — `Scripts` section
- `Program.cs` — register `ScriptOptions`, `ScriptEngine`, `ScriptEffectApplier`, `ScriptHandler` (4th `INativeHandler`)

---

## Task 1: Add Jint + `ScriptEngine` (sandboxed evaluation)

**Files:**
- Modify: `src/toimi.tools.tietue/toimi.tools.tietue.csproj`
- Create: `src/toimi.tools.tietue/Scripts/ScriptEngine.cs`
- Test: `src/toimi.tools.tietue.Tests/ScriptEngineTests.cs`

The `ScriptEngine` runs an AI-authored script body in Jint. Contract: the script body executes with a `data` variable (the entity's `Data` parsed to a JS object) in scope and `return`s an effects object. The host wraps it as an IIFE and `JSON.stringify`s the return value, so the .NET boundary is a JSON string. Guards: a wall-clock timeout, a statement cap, and a memory cap; **CLR access is NOT enabled** (Jint's default — do not call `AllowClr`).

- [ ] **Step 1: add the package.** In `toimi.tools.tietue.csproj`, add to the package `<ItemGroup>`:
```xml
    <PackageReference Include="Jint" Version="4.1.0" />
```
> If `4.1.0` is unavailable on restore, use the latest stable `4.x` (`dotnet add src/toimi.tools.tietue package Jint`) and keep the resolved version.

- [ ] **Step 2: failing tests.** `src/toimi.tools.tietue.Tests/ScriptEngineTests.cs`:
```csharp
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEngineTests
{
  private readonly ScriptEngine _engine = new();

  [Fact]
  public void Returns_effects_json_from_script_using_data()
  {
    var effects = _engine.Evaluate(
      "return { notify: { message: 'hi ' + data.name } };",
      """{"name":"Jari"}""");
    Assert.Contains("hi Jari", effects);
  }

  [Fact]
  public void Script_with_no_return_yields_empty_object()
  {
    var effects = _engine.Evaluate("var x = 1;", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Has_no_clr_or_io_access()
  {
    // System / reflection are not exposed in the sandbox — referencing them throws (caught → "{}").
    var effects = _engine.Evaluate("return { x: System.IO.File }", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Infinite_loop_is_terminated_by_guard()
  {
    // Should not hang; the statement/timeout guard aborts → caught → "{}".
    var effects = _engine.Evaluate("while(true){}", """{}""");
    Assert.Equal("{}", effects);
  }

  [Fact]
  public void Malformed_script_yields_empty_object()
  {
    Assert.Equal("{}", _engine.Evaluate("this is not js", """{}"""));
  }
}
```

- [ ] **Step 3: run, confirm FAIL.**

- [ ] **Step 4: implement `src/toimi.tools.tietue/Scripts/ScriptEngine.cs`:**
```csharp
using Jint;
using Jint.Runtime;

namespace toimi.tools.tietue.Scripts;

public class ScriptEngine
{
  // Evaluates an AI-authored script body with `data` (parsed from dataJson) in scope.
  // The body should `return` an effects object. Returns its JSON ("{}" on any failure/empty).
  public string Evaluate(string source, string dataJson)
  {
    try
    {
      var engine = new Engine(options => options
        .TimeoutInterval(TimeSpan.FromSeconds(2))
        .LimitMemory(8_000_000)
        .MaxStatements(10_000)
        .Strict());

      engine.SetValue("__dataJson", dataJson);
      var wrapped = $"JSON.stringify(((data) => {{ {source} }})(JSON.parse(__dataJson)) || {{}})";
      var result = engine.Evaluate(wrapped);
      var json = result.IsString() ? result.AsString() : "{}";
      return string.IsNullOrWhiteSpace(json) || json == "null" ? "{}" : json;
    }
    catch (Exception ex) when (ex is JavaScriptException or TimeoutException or StatementsCountOverflowException or MemoryLimitExceededException or ParserException)
    {
      return "{}";
    }
  }
}
```
> Verify the exact Jint 4.x exception type names and options API (`TimeoutInterval`, `LimitMemory`, `MaxStatements`, `Strict`) against the resolved package — adjust names if they differ (e.g. the overflow/parser exception types). The contract is: never throw, never hang, never touch CLR/IO; return effects JSON or `"{}"`. Keep the tests unchanged. If a specific guard exception type doesn't exist in the resolved version, broaden the `catch` to `catch (Exception)` returning `"{}"` (the sandbox must be fail-safe).

- [ ] **Step 5: run, confirm 5 PASS.** (The infinite-loop test must complete quickly — if it hangs, the statement/timeout guard isn't wired; fix the options.)

- [ ] **Step 6: format (verify exit 0) + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj; dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo MAIN=$?; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo TESTS=$?'
git add -A src/toimi.tools.tietue src/toimi.tools.tietue.Tests
git commit -m "feat(tietue): add sandboxed Jint script engine"
```

## Your Job (every task)
TDD where tests are given. Before committing: `dotnet format` apply both projects, then `--verify-no-changes` exit 0 on BOTH (hand-fix IDE0046 — rewrite the flagged `if`-return into a conditional expression). `git add -A` the tietue dirs; confirm clean tree. Report Status, test/suite counts, MAIN/TESTS verify exit codes, commit SHA, concerns.

---

## Task 2: `ScriptEffects` — parse the effects JSON

**Files:**
- Create: `src/toimi.tools.tietue/Scripts/ScriptEffects.cs`
- Test: `src/toimi.tools.tietue.Tests/ScriptEffectsTests.cs`

`ScriptEffects.Parse(string effectsJson) → ScriptEffects` turns the script's returned JSON into a typed record with optional members: `SetField (path,value-json)`, `Notify (message,title?,priority?)`, `Trigger (scheduleJson, handlerKind, handlerConfigJson?)`, `Escalate (prompt)`. Unknown keys ignored; malformed JSON → an all-null `ScriptEffects`.

- [ ] **Step 1: failing tests.** `src/toimi.tools.tietue.Tests/ScriptEffectsTests.cs`:
```csharp
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEffectsTests
{
  [Fact]
  public void Parses_notify_and_setfield()
  {
    var e = ScriptEffects.Parse("""{"notify":{"message":"hi","priority":"high"},"setField":{"path":"status","value":"done"}}""");
    Assert.Equal("hi", e.Notify!.Message);
    Assert.Equal("high", e.Notify.Priority);
    Assert.Equal("status", e.SetField!.Path);
    Assert.Equal("\"done\"", e.SetField.ValueJson);
  }

  [Fact]
  public void Parses_escalate_string()
  {
    var e = ScriptEffects.Parse("""{"escalate":"check the price trend"}""");
    Assert.Equal("check the price trend", e.Escalate);
  }

  [Fact]
  public void Empty_or_malformed_yields_no_effects()
  {
    Assert.Null(ScriptEffects.Parse("{}").Notify);
    Assert.Null(ScriptEffects.Parse("not json").Escalate);
  }
}
```

- [ ] **Step 2: run, confirm FAIL.**

- [ ] **Step 3: implement `src/toimi.tools.tietue/Scripts/ScriptEffects.cs`:**
```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Scripts;

public record SetFieldEffect(string Path, string ValueJson);
public record NotifyEffect(string Message, string? Title, string? Priority);
public record TriggerEffect(string ScheduleJson, string HandlerKind, string? HandlerConfigJson);

public record ScriptEffects(
  SetFieldEffect? SetField,
  NotifyEffect? Notify,
  TriggerEffect? Trigger,
  string? Escalate)
{
  public static ScriptEffects Parse(string effectsJson)
  {
    try
    {
      using var doc = JsonDocument.Parse(effectsJson);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return Empty;
      }

      return new ScriptEffects(
        ParseSetField(root),
        ParseNotify(root),
        ParseTrigger(root),
        Str(root, "escalate"));
    }
    catch (JsonException)
    {
      return Empty;
    }
  }

  private static readonly ScriptEffects Empty = new(null, null, null, null);

  private static SetFieldEffect? ParseSetField(JsonElement root)
  {
    if (!root.TryGetProperty("setField", out var sf) || sf.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    var path = Str(sf, "path");
    return path is null || !sf.TryGetProperty("value", out var v) ? null : new SetFieldEffect(path, v.GetRawText());
  }

  private static NotifyEffect? ParseNotify(JsonElement root)
  {
    if (!root.TryGetProperty("notify", out var n) || n.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    var message = Str(n, "message");
    return message is null ? null : new NotifyEffect(message, Str(n, "title"), Str(n, "priority"));
  }

  private static TriggerEffect? ParseTrigger(JsonElement root)
  {
    if (!root.TryGetProperty("trigger", out var t) || t.ValueKind != JsonValueKind.Object)
    {
      return null;
    }

    var kind = Str(t, "handlerKind");
    return kind is null || !t.TryGetProperty("schedule", out var s)
      ? null
      : new TriggerEffect(s.GetRawText(), kind, t.TryGetProperty("handlerConfig", out var c) ? c.GetRawText() : null);
  }

  private static string? Str(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 4: run, confirm 3 PASS. Format (verify exit 0) + commit:**
```
git commit -m "feat(tietue): add script effects parser"
```

---

## Task 3: `ScriptEffectApplier` — apply allowed effects

**Files:**
- Create: `src/toimi.tools.tietue/Scripts/ScriptEffectApplier.cs`
- Test: `src/toimi.tools.tietue.Tests/ScriptEffectApplierTests.cs`

Applies a `ScriptEffects` for an entity, **gated by a capabilities allowlist** (`setField`/`notify`/`trigger`/`escalate`). Uses existing services: `EntityRepository.UpdateAsync(Guid, JsonNode?, string[]?, ct)` (ns `toimi.tools.tietue.Entities`), `INotifier.SendAsync(message, title, priority, tags, ct)` (ns `toimi.tools.tietue.Notifications`), `TriggerRepository.CreateAsync(entityId, scheduleJson, handlerKind, handlerConfig, now, ct)` (ns `toimi.tools.tietue.Scheduling`), `IAgentRunner.RunAsync(entity, prompt, ct)` (ns `toimi.tools.tietue.Agents`). Returns a short list of the applied effect names (for the handler result/log). An effect whose capability is NOT granted is skipped.

- [ ] **Step 1: failing tests** (fakes for notifier + agent runner; real in-memory repos). `src/toimi.tools.tietue.Tests/ScriptEffectApplierTests.cs`:
```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptEffectApplierTests
{
  private const string Schema = """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"}}}""";

  private static async Task<(toimi.tools.tietue.Data.Entity entity, EntityRepository entities, FakeNotifier notifier, FakeAgentRunner runner, TriggerRepository triggers, ScriptEffectApplier applier)> SetupAsync(toimi.tools.tietue.Data.TietueDbContext db)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"x","status":"open"}"""), []);
    var notifier = new FakeNotifier();
    var runner = new FakeAgentRunner();
    var triggers = new TriggerRepository(db);
    return (e, entities, notifier, runner, triggers, new ScriptEffectApplier(entities, notifier, triggers, runner));
  }

  [Fact]
  public async Task Applies_setfield_when_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse("""{"setField":{"path":"status","value":"done"}}""");

    var applied = await applier.ApplyAsync(e, effects, ["setField"]);

    Assert.Contains("setField", applied);
    var reloaded = await entities.GetAsync(e.Id);
    Assert.Equal("done", reloaded!.Data.RootElement.GetProperty("status").GetString());
  }

  [Fact]
  public async Task Skips_effect_when_capability_not_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse("""{"notify":{"message":"hi"}}""");

    var applied = await applier.ApplyAsync(e, effects, []); // no capabilities granted

    Assert.Empty(applied);
    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Applies_notify_and_escalate_when_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse("""{"notify":{"message":"hi","priority":"high"},"escalate":"think hard"}""");

    var applied = await applier.ApplyAsync(e, effects, ["notify", "escalate"]);

    Assert.Equal("hi", notifier.Sent.Single().Message);
    Assert.Equal("think hard", runner.Runs.Single().Prompt);
    Assert.Contains("notify", applied);
    Assert.Contains("escalate", applied);
  }

  [Fact]
  public async Task Applies_trigger_when_granted()
  {
    using var db = TestDb.New();
    var (e, entities, notifier, runner, triggers, applier) = await SetupAsync(db);
    var effects = ScriptEffects.Parse("""{"trigger":{"schedule":{"at":"2026-07-01T09:00:00Z"},"handlerKind":"notify","handlerConfig":{"titleTemplate":"{name}"}}}""");

    var applied = await applier.ApplyAsync(e, effects, ["trigger"]);

    Assert.Contains("trigger", applied);
    Assert.Single(await triggers.ListByEntityAsync(e.Id));
  }
}
```

- [ ] **Step 2: run, confirm FAIL.**

- [ ] **Step 3: implement `src/toimi.tools.tietue/Scripts/ScriptEffectApplier.cs`:**
```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Notifications;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Scripts;

public class ScriptEffectApplier(EntityRepository entities, INotifier notifier, TriggerRepository triggers, IAgentRunner runner)
{
  public async Task<IReadOnlyList<string>> ApplyAsync(Entity entity, ScriptEffects effects, string[] capabilities, CancellationToken ct = default)
  {
    var granted = capabilities.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var applied = new List<string>();

    if (effects.SetField is { } sf && granted.Contains("setField"))
    {
      var data = JsonNode.Parse(entity.Data.RootElement.GetRawText())!.AsObject();
      data[sf.Path] = JsonNode.Parse(sf.ValueJson);
      await entities.UpdateAsync(entity.Id, data, null, ct);
      applied.Add("setField");
    }

    if (effects.Notify is { } n && granted.Contains("notify"))
    {
      await notifier.SendAsync(n.Message, n.Title, n.Priority ?? "default", null, ct);
      applied.Add("notify");
    }

    if (effects.Trigger is { } t && granted.Contains("trigger"))
    {
      await triggers.CreateAsync(entity.Id, t.ScheduleJson, t.HandlerKind, t.HandlerConfigJson, DateTimeOffset.UtcNow, ct);
      applied.Add("trigger");
    }

    if (effects.Escalate is { } prompt && granted.Contains("escalate"))
    {
      await runner.RunAsync(entity, prompt, ct);
      applied.Add("escalate");
    }

    return applied;
  }
}
```
> Note `setField` re-fetches the entity inside `UpdateAsync` (re-validates against the type schema and re-indexes), so a script can't write data that violates the schema. Effects are applied in a fixed order (data → notify → trigger → escalate).

- [ ] **Step 4: run, confirm 4 PASS. Format (verify exit 0) + commit:**
```
git commit -m "feat(tietue): add capability-gated script effect applier"
```

---

## Task 4: `ScriptHandler` (kind `"script"`) + kill switch

**Files:**
- Create: `src/toimi.tools.tietue/Scripts/ScriptOptions.cs`
- Create: `src/toimi.tools.tietue/Handlers/ScriptHandler.cs`
- Test: `src/toimi.tools.tietue.Tests/ScriptHandlerTests.cs`

`ScriptHandler` (ns `toimi.tools.tietue.Handlers`, kind `"script"`) reads its config `{ "source": "<js>", "capabilities": ["setField",...] }`, evaluates the script against the entity's `Data` via `ScriptEngine`, parses the effects, applies the granted ones via `ScriptEffectApplier`, and returns `HandlerResult("ran", <json {applied:[...]}>)`. If `ScriptOptions.Enabled` is false (kill switch), it returns `HandlerResult("disabled")` without running.

- [ ] **Step 1: `ScriptOptions`.** `src/toimi.tools.tietue/Scripts/ScriptOptions.cs`:
```csharp
namespace toimi.tools.tietue.Scripts;

public class ScriptOptions
{
  public bool Enabled { get; set; } = true;
}
```

- [ ] **Step 2: failing tests.** `src/toimi.tools.tietue.Tests/ScriptHandlerTests.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Scripts;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScriptHandlerTests
{
  private const string Schema = """{"type":"object","properties":{"status":{"type":"string"},"name":{"type":"string"}}}""";

  private static async Task<(toimi.tools.tietue.Data.Entity e, FakeNotifier notifier, ScriptHandler handler)> SetupAsync(toimi.tools.tietue.Data.TietueDbContext db, bool enabled = true)
  {
    await new TypeRepository(db).DefineAsync("task", Schema);
    var entities = new EntityRepository(db, new SchemaValidator());
    var e = await entities.CreateAsync("task", JsonNode.Parse("""{"name":"Jari","status":"open"}"""), []);
    var notifier = new FakeNotifier();
    var applier = new ScriptEffectApplier(entities, notifier, new TriggerRepository(db), new FakeAgentRunner());
    var handler = new ScriptHandler(new ScriptEngine(), applier, new ScriptOptions { Enabled = enabled });
    return (e, notifier, handler);
  }

  [Fact]
  public async Task Runs_script_and_applies_granted_effects()
  {
    using var db = TestDb.New();
    var (e, notifier, handler) = await SetupAsync(db);
    var config = """{"source":"return { notify: { message: 'hello ' + data.name } };","capabilities":["notify"]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("ran", result.Status);
    Assert.Equal("hello Jari", notifier.Sent.Single().Message);
    Assert.Contains("notify", result.Result);
  }

  [Fact]
  public async Task Does_not_apply_ungranted_effects()
  {
    using var db = TestDb.New();
    var (e, notifier, handler) = await SetupAsync(db);
    var config = """{"source":"return { notify: { message: 'x' } };","capabilities":[]}""";

    await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Empty(notifier.Sent);
  }

  [Fact]
  public async Task Disabled_kill_switch_skips_execution()
  {
    using var db = TestDb.New();
    var (e, notifier, handler) = await SetupAsync(db, enabled: false);
    var config = """{"source":"return { notify: { message: 'x' } };","capabilities":["notify"]}""";

    var result = await handler.HandleAsync(new HandlerContext(e, config, DateTimeOffset.UtcNow));

    Assert.Equal("disabled", result.Status);
    Assert.Empty(notifier.Sent);
  }
}
```

- [ ] **Step 3: run, confirm FAIL.**

- [ ] **Step 4: implement `src/toimi.tools.tietue/Handlers/ScriptHandler.cs`:**
```csharp
using System.Text.Json;
using toimi.tools.tietue.Scripts;

namespace toimi.tools.tietue.Handlers;

public class ScriptHandler(ScriptEngine engine, ScriptEffectApplier applier, ScriptOptions options) : INativeHandler
{
  public string Kind => "script";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    if (!options.Enabled)
    {
      return new HandlerResult("disabled");
    }

    string source = "", capabilitiesRaw = "[]";
    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      if (cfg.RootElement.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String)
      {
        source = s.GetString() ?? "";
      }

      if (cfg.RootElement.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
      {
        capabilitiesRaw = c.GetRawText();
      }
    }

    var capabilities = JsonSerializer.Deserialize<string[]>(capabilitiesRaw) ?? [];
    var effectsJson = engine.Evaluate(source, ctx.Entity.Data.RootElement.GetRawText());
    var effects = ScriptEffects.Parse(effectsJson);
    var applied = await applier.ApplyAsync(ctx.Entity, effects, capabilities, ct);

    return new HandlerResult("ran", JsonSerializer.Serialize(new { applied }));
  }
}
```

- [ ] **Step 5: run, confirm 3 PASS + full suite green. Format (verify exit 0) + commit:**
```
git commit -m "feat(tietue): add sandboxed script handler with capability gating and kill switch"
```

---

## Task 5: Config + Program wiring

**Files:**
- Modify: `src/toimi.tools.tietue/appsettings.json`, `src/toimi.tools.tietue/Program.cs`

- [ ] **Step 1: config.** In `src/toimi.tools.tietue/appsettings.json`, add a top-level section (additive):
```json
  "Scripts": {
    "Enabled": true
  }
```

- [ ] **Step 2: register in `Program.cs`.** After the existing handler registrations (and before `AddMcpServer(...)`), add:
```csharp
builder.Services.AddSingleton(
  builder.Configuration.GetSection("Scripts").Get<toimi.tools.tietue.Scripts.ScriptOptions>() ?? new toimi.tools.tietue.Scripts.ScriptOptions());
builder.Services.AddSingleton<toimi.tools.tietue.Scripts.ScriptEngine>();
builder.Services.AddScoped<toimi.tools.tietue.Scripts.ScriptEffectApplier>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.ScriptHandler>();
```
(`ScriptHandler` becomes the FOURTH scoped `INativeHandler` — notify, set-field, message, script — all resolved by the scoped `HandlerRegistry`. `ScriptEngine` + `ScriptOptions` are singletons (stateless / config); `ScriptEffectApplier` is scoped because it depends on the scoped `EntityRepository`/`TriggerRepository`.)

- [ ] **Step 3: run the FULL suite** (the admin tests boot the real Program; `ScriptOptions` binds, `ScriptHandler` resolves; the worker tick finds no script triggers in the empty in-memory DB). Confirm all pass.

- [ ] **Step 4: validate appsettings JSON, format (verify exit 0), commit:**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add -A src/toimi.tools.tietue
git commit -m "feat(tietue): wire script options, engine, applier, and handler"
```

---

## Task 6: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: tietue suite + lint (real exit codes).**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo "MAIN=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo "TESTS=$?"
'
```
Expected: all tests pass (Phases 1–4 plus the new Phase 5 tests), `MAIN=0`, `TESTS=0`.

- [ ] **Step 2: manual smoke (optional, recommended).** Define a custom type with a `script`-handler trigger, e.g. via `set_trigger(entityId, {"at":"<soon>"}, "script", {"source":"return { notify: { message: 'status is ' + data.status } };","capabilities":["notify"]})`; wait for the worker tick; confirm the notification fired and an `entity_events` row (`kind=script`) was recorded with the applied effects. Then test a script that returns `{escalate:"..."}` with the `escalate` capability and confirm an agent run happened. Then flip `Scripts:Enabled=false` and confirm script triggers no-op.

- [ ] **Step 3: final commit if anything changed.**
```bash
git add -A && git commit -m "chore(tietue): phase 5 script handler complete" --allow-empty
```

---

## Phase 5 Done — what exists now

A trigger can fire **AI-authored JavaScript** in a sandbox (Jint, no CLR/IO, time/statement/memory caps): the script is a pure function of the entity's `Data` that returns a declarative **effects** object, and the host applies only the effects the script's **capability grant** allows (`setField`/`notify`/`trigger`/`escalate`) — with a global `Scripts:Enabled` kill switch. `escalate` hands off to the Phase 4 `AgentRunner`, completing the handler cost ladder (deterministic built-in → custom script → escalate-to-agent). The native palette is now complete: `notify`, `set-field`, `poll-diff` (deferred), `message`, and `script`.

**Deferred (noted inline):** a `fetch` capability (network egress — add behind a domain allowlist with the deferred `poll-diff`); script `validate`/`derive`/`renderMessage` lifecycle hooks; per-script/per-type kill switches; a WASM/Extism runtime (the host boundary is JSON-string-based, so the engine is swappable).

**Next phase (separate plan):**
- **Phase 6 — Cutover:** delete the muistio/taidot/muistutin/ajastin pods, DBs, and k8s bases; drop the retired servers from the web + agent `McpServers` lists and the admin `Tools` list (keep koti, verkko, tietue); migrate any existing data if needed; update standard-skill seeds. This is the final step that realizes the "6 stateful pods → tietue + koti + verkko" consolidation from the design study.
