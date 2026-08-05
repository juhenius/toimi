# Schedule Value Type + Handler ValidateConfig Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate triggers where they are written. Promote the schedule grammar (`{"at"}` one-shot / `{"start","rrule","tz"}` recurring) from the private `Schedules.Spec` record to a public `Schedule` value type owned by one parser; move stamp-tz-then-validate into `TriggerRepository.CreateAsync`/`UpdateAsync` (throwing `TietueValidationException` instead of silently persisting disabled triggers); give `INativeHandler` a `ValidateConfig` member so `set_trigger`/`update_trigger`/`define_type` reject configs their handlers can't do useful work with. Closes two silent-failure gaps: (1) `update_trigger` accepts a schedule `set_trigger` rejects, stamps `NextFireAt = null`, and persists `Enabled=false` behind a success-shaped response (`UpdateTriggerTool.cs:23` has no validation while `SetTriggerTool.cs:39-49` duplicates it); (2) a typo'd `promptTemplate` arms a `message` trigger that fires a full agent run with an empty prompt forever (`TemplateRenderer` renders missing templates as `""`).

**Architecture:** A new `Scheduling/Schedule.cs` sealed class is the single owner of the schedule grammar: `Parse(string) → Schedule?` (the only JSON→spec parse in the codebase), typed factories `OneShotAt(DateTimeOffset)` / `Recurring(start, rrule, tz)` for the three programmatic builders (`ActivateTool`, `ExpiryReconciler`, `TriggerProvisioner` stop hand-building JSON), `WithDefaultTz` (the one resolve rule), `TryValidate` (grammar + rrule syntax + the unsupported sub-daily rule), `NextOnOrAfter`/`NextAfter` (fire-time math, delegating to `RecurrenceCalculator`), and `ToJson` (returns the retained source JSON so storage stays compatible). The static `Schedules` class and `SchedulesTests` are **deleted**; `SchedulerTick` parses via `Schedule`. `TriggerRepository` keeps its string-based `CreateAsync`/`UpdateAsync` signatures (93 test call sites) but they now parse-or-throw and validate-or-throw; a typed `CreateAsync(Guid, Schedule, ...)` overload serves the programmatic builders. MCP tools keep their return-string convention by catching `TietueValidationException` (the `DefineTypeTool` precedent). `INativeHandler.ValidateConfig(string?) → ValidationResult` is a default interface method returning `Valid()` (the 5 test stub handlers keep compiling; `DeleteHandler` genuinely accepts anything); `NotifyHandler`/`MessageHandler`/`SetFieldHandler`/`ScriptHandler` override it with the minimal checks their `HandleAsync` needs. `TypeRepository.DefineAsync` gains structural validation of `DefaultTriggers` templates via a new `Provisioning/TriggerTemplates.Validate` (kind + config validated against `HandlerRegistry` when available). `run_trigger`/`OccurrenceRunner`/`SchedulerTick` never validate — they fire whatever exists.

**Tech Stack:** .NET 10, xUnit v2, EF Core (InMemory in unit tests), Ical.Net 5.2.3 (`RecurrencePattern`), Npgsql, Testcontainers (Docker-gated via `DockerFactAttribute`).

## Global Constraints

- dotnet is NOT on PATH: every command uses `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"` first.
- Test command: `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` (use `--filter` per task where possible; the final gate runs the whole project plus core and web).
- Before the commit of each task: `dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj` and `dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`, then verify `--verify-no-changes` exits 0 on both. Enforced as errors: IDE0005 (unused usings), IDE0022 (block bodies — no expression-bodied members, **including the default interface method** in Task 3), IDE0046, whitespace.
- Commit style: `<type>(<scope>): <subject>`, e.g. `refactor(tietue): ...`.
- 2-space indent, file-scoped namespaces; comments only for constraints the code can't show.
- tietue suite is currently 325 tests (Docker-gated skips count when Docker is absent); the count must never end a task below 325. Task 1 adds ~23 before Task 2 removes the 14 `SchedulesTests` cases. Expected final ≈ 360. Core (93) and web (38) suites are untouched — verify at the gate.
- MCP tool surface: same tool names and parameter shapes; errors return as strings, never as exceptions escaping a tool. Error *text* may improve where noted (each change listed explicitly with its test update).
- Trigger semantics unchanged for valid inputs: same `NextFireAt` computations, same copy-down provisioning results, same storage format for JSON-authored schedules (programmatic builders switch to a documented-compatible canonical form — see Design Decisions).
- Seeded types (`TypeSeeder`) must still seed: their DefaultTriggers/configs are the reference examples of valid input and Task 5 adds a test proving they pass the full validation with a real `HandlerRegistry`.

## Design Decisions

**`Schedule` retains its source JSON; programmatic factories canonicalize.** `Parse` keeps the exact input string and `ToJson` returns it (with `WithDefaultTz` editing via `JsonNode` exactly as `Schedules.WithDefaultTimeZone` does today), so every JSON-authored schedule is stored byte-identically to today — including unknown extra keys. `OneShotAt`/`Recurring` serialize dates as `ToUniversalTime().ToString("o")`. That changes the *string* stored by `ExpiryReconciler`/`TriggerProvisioner`/`ActivateTool` (e.g. `"2030-01-01T06:00:00Z"` → `"2030-01-01T06:00:00.0000000+00:00"`) but not the instant: every reader goes through `Schedule.Parse`, which parses both forms. One test asserts the old raw substring (`JobEndToEndTests.cs:45`) and is loosened to the prefix without the `Z`. This is the documented-compatible canonicalization the constraints allow.

**Invalid vs. exhausted — two distinct rejections, both enforced at write time.** `TryValidate` covers only what is *provably invalid* regardless of clock: grammar (neither `at` nor `start`+`rrule`), an rrule `RecurrencePattern` cannot parse (today this crashes out of `Schedules.InitialNextFireAt` uncaught — a real gap closed), and the sub-daily+BY-parts+DST-tz combination `RecurrenceCalculator` refuses (`IsUnsupportedSubDaily`). *Exhausted* means valid grammar but `NextOnOrAfter(now)` is null — e.g. a COUNT/UNTIL rrule already spent (`RecurrenceCalculator.ArithmeticNext` returns null past COUNT; the Ical.Net window search finds nothing). Today's `SetTriggerTool` rejects **both** (`HasUnsupportedSubDailyRule` at :40 and the `InitialNextFireAt is null` check at :46), so rejecting both in the repository is parity, not over-rejection — but with *distinct messages* so the agent can tell a typo from a spent recurrence. Crucially NOT rejected: a one-shot `at` in the past. `NextOnOrAfter` returns the `at` unconditionally (immediately due) — `ExpiryReconciler`'s past-expiry semantics and `ExpiryReconcilerTests.Past_expiry_date_arms_an_immediately_due_trigger` depend on it. Scheduler-side exhaustion (`SchedulerTick:53-54`: `NextAfter` → null → disable after the last fire) is legitimate lifecycle, untouched.

**Throw in the repository, catch in the tools.** `CreateAsync`/`UpdateAsync` throw `TietueValidationException` (the type `TypeRepository`/`EntityRepository` already throw; `DefineTypeTool` already catches it and returns `string.Join("; ", ex.Errors)`). Tools keep the MCP return-string convention with the same catch. This makes the repository the choke point: `UpdateTriggerTool` gets the protection without writing any validation itself, and no future writer can forget it. The invariant after this change: **every persisted trigger is born `Enabled=true` with a non-null `NextFireAt`** — `CreateAsync`'s silent-disable branch (`Enabled = nextFireAt is not null`, :24) is deleted, and `TriggerRepositoryTests.Create_with_unresolvable_schedule_yields_a_disabled_trigger` becomes a throws-test. `UpdateAsync` validates and computes *before* mutating the tracked row, so a throw leaves no half-applied changes for a later `SaveChangesAsync` in the same DI scope to sweep up. The existing re-enable recompute path (`UpdateAsync:77-82`, the third resolve-rule variant) survives but now parses via `Schedule.Parse` — its refuse-to-re-enable-exhausted semantics are tested and unchanged.

**String overloads stay; the typed overload serves programmatic builders.** 93 test call sites pass JSON strings to `CreateAsync`/`UpdateAsync`; keeping string entry points (which delegate to the typed path after `ParseOrThrow`) avoids pure churn. `ActivateTool`/`ExpiryReconciler`/`TriggerProvisioner` use `CreateAsync(Guid, Schedule, ...)` with `Schedule.OneShotAt`/`Recurring` — hand-built JSON disappears, and the type makes "unparsed string" unrepresentable on those paths. `UpdateAsync` needs no typed overload (only the tool and repository tests call it).

**`ExpiryReconciler`/`TriggerProvisioner` parse dates themselves and skip loudly, not silently.** Both read date *values* from entity data. A garbage value (`"expiresAt":"soon"`) today produces a disabled zombie trigger row; with a throwing `CreateAsync` it would instead fail the whole entity create from inside the behavior pipeline — unacceptable. So: `ExpiryReconciler.ExpiryAt` returns `DateTimeOffset?` via `TryParse` (garbage → no trigger at all, strictly better than the zombie row); `TriggerProvisioner.BuildSchedule` returns `Schedule?` the same way, and `ProvisionAsync` additionally wraps `CreateAsync` in a `catch (TietueValidationException)` that logs a warning and skips that template (covers data-dependent exhaustion, e.g. a reminder created with an already-spent `COUNT` rrule) — the entity create always survives. `TriggerProvisioner` gains an optional `ILogger<TriggerProvisioner>?` ctor param (NullLogger fallback, the `OccurrenceRunner` pattern) so its 8 test construction sites compile unchanged. `ActivateTool` needs no catch: `OneShotAt` of a parsed `DateTimeOffset` is valid by construction and one-shots always resolve.

**`ValidateConfig` is a default interface method returning `Valid()`.** Five test stub handlers (`ThrowingHandler` ×3, `ExplodingHandler`, `LeaseObservingHandler`) implement `INativeHandler` and must keep compiling; a DIM also encodes the honest default — "any config is acceptable" — which is exactly right for `DeleteHandler` (its `HandleAsync` never reads config, so no override). Per-handler rules are derived strictly from what each `HandleAsync` needs to do useful work:

| Handler | Rule | Justification from `HandleAsync` |
|---|---|---|
| `delete` | accept anything (no override) | config is never read |
| `notify` | config must be a JSON object with `titleTemplate` and/or `messageTemplate` a non-empty string; `titleTemplate`/`messageTemplate`/`priority`/`tags` must be strings when present | with neither template, `Render` yields `""` and an empty notification is sent every fire; `Str()` (:34-37) silently drops non-string values, so a `tags` array is a swallowed typo; a non-object config throws `InvalidOperationException` from `TryGetProperty` at fire time |
| `message` | config must be a JSON object with non-empty `promptTemplate` string | missing/null → `Render(null)` → `""` → full agent run with an empty prompt, forever (finding 4's teeth) |
| `set-field` | config must be a JSON object with non-empty `path` string | `HandleAsync` returns `"skipped"` without it (:15-18) — a trigger that can never do work; `value` stays unvalidated because a missing value is a genuine null-assignment (:20) |
| `script` | config must be a JSON object; `fromEntity:true` alone is valid (entity is authoritative, inline fields ignored — `Resolve` :123); otherwise `source` non-empty string required; `allowedHosts`/`capabilities` must be arrays of strings when present | null config or no source → `"error":"no script source configured"` every fire (:38-41); `StrArray` (:162-167) silently coerces wrong shapes to `[]`, so a string-valued `allowedHosts` becomes a script with no egress |

Deliberately NOT validated (would be invented requirements): template token names (a `{token}` may legitimately reference an optional field — `TemplateRenderer` rendering absent tokens as `""` is its documented behavior for *individual tokens*; the failure mode being closed is the *whole prompt/message* being empty), `priority` values (ntfy accepts arbitrary strings), script `source` content, `value` shape.

**Write-time enforcement only; provision-time stays a logged skip.** `SetTriggerTool` (already holds `HandlerRegistry`) calls `handler.ValidateConfig(handlerConfig)` after the kind check; `UpdateTriggerTool` gains a `HandlerRegistry` ctor param and, when `handlerConfig` is provided, fetches the trigger first to resolve its `HandlerKind` (an unresolvable legacy kind skips config validation — blocking edits on a trigger that already can't run would help nobody; `run_trigger`'s unknown-kind path records the error event, per C1). `TriggerProvisioner` does NOT call `ValidateConfig` at provision time: after Task 5, templates with bad kinds/configs can no longer *enter* `DefaultTriggers` through `define_type`, and re-validating per-entity would only convert a working copy-down into a latency tax. Pre-existing DB rows with bad configs still fire and surface as error events — visible, not silent.

**`define_type`-time structural validation IS feasible — decision: validate there.** A DefaultTriggers template is `{"when":{"atField","rruleField"?,"tzField"?},"handler":{"kind","config"?}}`. The `when` fields are FIELD REFERENCES resolved per-entity, so schedule *content* cannot be validated at define time — but their structure can (`atField` a non-empty string, the optional fields strings). The handler `config` is *literal* JSON in which `{token}` placeholders are just strings, so `handler.ValidateConfig` applies directly (`{"promptTemplate":"{prompt}"}` is a non-empty string — passes). New `Provisioning/TriggerTemplates.Validate(json, HandlerRegistry?)` does structure always, kind+config when a registry is available. `TypeRepository` gains `HandlerRegistry? handlers = null` (the arch-4 optional-ctor-param pattern: the 62 bare `new TypeRepository(db)` test constructions compile unchanged and run structure-only checks; Microsoft DI injects the scoped registry in prod, so `DefineTypeTool` and boot-time `TypeSeeder` always get full validation). The one thing that remains per-entity-only: garbage date *values* in entity data — the honest enforcement point for that is the type's JSON Schema (`format`), not the template; the provisioner's `TryParse`-skip covers it (see above).

**`SetTriggerTool` sheds two dependencies' worth of duplication.** Its stamp-then-validate block (:36-49, held in sync with `TriggerRepository` only by the comment at :36-38) is deleted along with its `ToimiConfiguration config` ctor param — the repository is now the only place that stamps and validates. Error-message texts the existing tests assert (`"not supported in DST timezones"` + `tz:"UTC"` hint, `"does not resolve to a future fire time"`) move verbatim into `Schedule.TryValidate` / `TriggerRepository.NeverFiresError`, so those assertions keep passing. One message deliberately changes: unparseable JSON now reports `"Invalid schedule JSON..."` instead of the misleading `"does not resolve to a future fire time"` (`SetTriggerToolTests.Rejects_malformed_schedule` updated).

**Per-test-file disposition (from grep):**

| File | Disposition |
|---|---|
| `SchedulesTests.cs` (14 cases) | **Deleted** in Task 2; superseded by `ScheduleTests.cs` (Task 1) |
| `TriggerRepositoryTests.cs` | `Create_with_unresolvable_schedule_yields_a_disabled_trigger` → throws-test; +2 new throws-tests (Task 2) |
| `UpdateTriggerToolTests.cs` | +3 regression tests written FIRST (Task 2); ctor gains `Handlers()` registry (Task 4) + 1 config-rejection test |
| `SetTriggerToolTests.cs` | ctor drops `TestConfig.Default` arg ×8 (Task 2); `Rejects_malformed_schedule` message updated (Task 2); null-config call sites get `{"titleTemplate":"hi"}` + 1 new config-rejection test (Task 4) |
| `TriggerToolsTests.cs` | `SetTriggerTool` ctor −1 arg (Task 2) |
| `ExpiryReconcilerTests.cs` | `Garbage_expiry_date...` strengthened to assert NO trigger row (Task 2) |
| `TriggerProvisionerTests.cs` | +2: garbage due-date → no trigger; exhausted COUNT rrule → logged skip, entity survives (Task 2) |
| `JobEndToEndTests.cs` | `:45` schedule-substring assertion loosened (Task 2) |
| `ActivateToolTests.cs` | unchanged (behavior identical) |
| Handler test files (`NotifyHandlerTests`, `MessageHandlerTests`, `SetFieldHandlerTests`, `ScriptHandlerTests`, `DeleteHandlerTests`) | +~13 `ValidateConfig` facts (Task 3) |
| `TypeRepositoryTests.cs` / `TypeSeederTests.cs` | +~7 template-validation facts incl. seeder-with-real-registry (Task 5) |
| All stub-handler files (`RunTriggerToolTests`, `SchedulerTickTests` ×2, `OccurrenceRunnerTests` ×2) | **Unchanged** — DIM keeps stubs compiling |
| All 62 bare `new TypeRepository(db)` sites | **Unchanged** — optional ctor param |

---

## Task 1: `Schedule` value type (TDD)

**Files**
- Create: `src/toimi.tools.tietue/Scheduling/Schedule.cs`
- Test (create): `src/toimi.tools.tietue.Tests/ScheduleTests.cs`
- `Scheduling/Schedules.cs` is NOT touched in this task — both coexist until Task 2 migrates callers.

**Interfaces**
- `public sealed class Schedule` with `DateTimeOffset? At`, `DateTimeOffset? Start`, `string? Rrule`, `string? Tz`, `bool IsRecurring`; static `Parse(string) → Schedule?`, `OneShotAt(DateTimeOffset)`, `Recurring(DateTimeOffset, string, string?)`; instance `WithDefaultTz(string)`, `TryValidate(out string?)`, `NextOnOrAfter(DateTimeOffset)`, `NextAfter(DateTimeOffset)`, `ToJson()`.

**Steps**

- [ ] Write the failing test file `src/toimi.tools.tietue.Tests/ScheduleTests.cs` (red: `Schedule` doesn't exist — compile failure). It ports every `SchedulesTests` case to the instance API and adds factory/validation/round-trip coverage:

```csharp
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ScheduleTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  private static Schedule Parsed(string json)
  {
    var s = Schedule.Parse(json);
    Assert.NotNull(s);
    return s;
  }

  [Fact]
  public void OneShot_next_on_or_after_is_the_at_time()
  {
    var s = Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""");
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
  }

  [Fact]
  public void OneShot_in_the_past_is_still_returned_immediately_due()
  {
    // Expiry depends on this: a past 'at' is due NOW, not invalid and not exhausted.
    var s = Parsed(/*lang=json,strict*/ """{"at":"2020-01-01T00:00:00Z"}""");
    Assert.Equal(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
    Assert.True(s.TryValidate(out _));
  }

  [Fact]
  public void OneShot_next_after_fire_is_null()
  {
    var s = Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""");
    Assert.Null(s.NextAfter(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Fact]
  public void Recurring_next_on_or_after_is_first_occurrence()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
  }

  [Fact]
  public void Recurring_next_after_is_following_occurrence()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");
    Assert.Equal(
      new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero),
      s.NextAfter(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Fact]
  public void Zoned_subdaily_recurring_returns_on_grid_now()
  {
    // Regression port from SchedulesTests: real user job {start 06:30Z, MINUTELY;INTERVAL=30,
    // Europe/Helsinki} OOMed InitialNextFireAt (Ical.Net 5.2.3 DST fall-back loop + OrderBy).
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-07-31T06:30:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30","tz":"Europe/Helsinki"}""");
    var next = s.NextOnOrAfter(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero));
    stopwatch.Stop();

    Assert.Equal(new DateTimeOffset(2026, 8, 2, 12, 0, 0, TimeSpan.Zero), next);
    Assert.True(stopwatch.ElapsedMilliseconds < 5000, $"took {stopwatch.ElapsedMilliseconds}ms — should be immediate");
  }

  [Fact]
  public void At_wins_over_start_and_rrule()
  {
    // Precedence port: a spec carrying both is one-shot (matches the old InitialNextFireAt/NextAfter).
    var s = Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z","start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), s.NextOnOrAfter(Now));
    Assert.Null(s.NextAfter(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Theory]
  [InlineData("{ not json")]
  [InlineData("[]")]
  [InlineData("5")]
  [InlineData("null")]
  [InlineData("\"daily\"")]
  [InlineData("true")]
  [InlineData(/*lang=json,strict*/ """{"at":"soon"}""")]
  public void Unparseable_or_non_object_yields_null(string json)
  {
    Assert.Null(Schedule.Parse(json));
  }

  [Fact]
  public void WithDefaultTz_stamps_recurring_spec_without_one()
  {
    var stamped = Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""")
      .WithDefaultTz("Europe/Helsinki");
    Assert.Equal("Europe/Helsinki", stamped.Tz);
    using var doc = System.Text.Json.JsonDocument.Parse(stamped.ToJson());
    Assert.Equal("Europe/Helsinki", doc.RootElement.GetProperty("tz").GetString());
    Assert.Equal("FREQ=DAILY", doc.RootElement.GetProperty("rrule").GetString());
  }

  [Fact]
  public void WithDefaultTz_leaves_one_shot_unchanged()
  {
    const string json = /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""";
    var s = Parsed(json);
    Assert.Same(s, s.WithDefaultTz("Europe/Helsinki"));
    Assert.Equal(json, s.ToJson());
  }

  [Fact]
  public void WithDefaultTz_leaves_existing_tz_unchanged()
  {
    const string json = /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY","tz":"America/New_York"}""";
    var s = Parsed(json);
    Assert.Same(s, s.WithDefaultTz("Europe/Helsinki"));
  }

  [Fact]
  public void ToJson_round_trips_the_source_including_unknown_keys()
  {
    // Storage compatibility: what the caller wrote is what gets persisted.
    const string json = /*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z","note":"keep me"}""";
    Assert.Equal(json, Parsed(json).ToJson());
  }

  [Fact]
  public void OneShotAt_factory_round_trips_through_parse()
  {
    var at = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
    var s = Schedule.OneShotAt(at);
    Assert.Equal(at, s.At);
    var reparsed = Schedule.Parse(s.ToJson());
    Assert.Equal(at, reparsed!.At);
  }

  [Fact]
  public void Recurring_factory_builds_start_rrule_and_optional_tz()
  {
    var start = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
    var s = Schedule.Recurring(start, "FREQ=DAILY", "Europe/Helsinki");
    Assert.Equal(start, s.Start);
    Assert.Equal("FREQ=DAILY", s.Rrule);
    Assert.Equal("Europe/Helsinki", s.Tz);
    var bare = Schedule.Recurring(start, "FREQ=DAILY", null);
    Assert.Null(bare.Tz);
    Assert.DoesNotContain("tz", bare.ToJson());
  }

  [Fact]
  public void Validate_rejects_spec_with_neither_at_nor_start_rrule()
  {
    Assert.False(Parsed("{}").TryValidate(out var error));
    Assert.Contains("at", error);
    Assert.False(Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z"}""").TryValidate(out _));
  }

  [Fact]
  public void Validate_rejects_subdaily_by_part_rule_in_dst_timezone()
  {
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"Europe/Helsinki"}""");
    Assert.False(s.TryValidate(out var error));
    Assert.Contains("not supported in DST timezones", error);
    Assert.Contains("tz:\"UTC\"", error);
  }

  [Fact]
  public void Validate_catches_subdaily_by_part_rule_after_default_tz_stamping()
  {
    // The order writers must use: stamp first, then validate — a tz-less rule is only
    // dangerous once the user's DST zone lands on it.
    var s = Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9"}""");
    Assert.True(s.TryValidate(out _));
    Assert.False(s.WithDefaultTz("Europe/Helsinki").TryValidate(out _));
  }

  [Fact]
  public void Validate_accepts_subdaily_plain_interval_and_utc_escape_hatch()
  {
    Assert.True(Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30","tz":"Europe/Helsinki"}""")
      .TryValidate(out _));
    Assert.True(Parsed(/*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"UTC"}""")
      .TryValidate(out _));
  }

  [Fact]
  public void Validate_accepts_valid_one_shot_and_recurring()
  {
    Assert.True(Parsed(/*lang=json,strict*/ """{"at":"2026-06-01T09:00:00Z"}""").TryValidate(out var e1));
    Assert.Null(e1);
    Assert.True(Parsed(/*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""").TryValidate(out _));
  }

  [Fact]
  public void Exhausted_count_rrule_is_valid_but_resolves_to_null()
  {
    // The invalid-vs-exhausted distinction: TryValidate passes, NextOnOrAfter says "spent".
    var s = Parsed(/*lang=json,strict*/ """{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}""");
    Assert.True(s.TryValidate(out _));
    Assert.Null(s.NextOnOrAfter(Now));
  }
}
```

- [ ] Run the new tests, confirm red (compile failure): `export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH" && dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter ScheduleTests`
- [ ] Create `src/toimi.tools.tietue/Scheduling/Schedule.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

/// <summary>
/// The single owner of the trigger schedule grammar: one-shot {"at":"&lt;iso utc&gt;"} or recurring
/// {"start":"&lt;iso utc&gt;","rrule":"FREQ=...","tz":"&lt;iana&gt;"}. Every instance is grammatically
/// parseable by construction (Parse or the typed factories); TryValidate covers the semantic
/// rules writers enforce. ToJson returns exactly the JSON the schedule was built from (plus any
/// stamped tz), so persisted schedules stay compatible with what callers wrote.
/// </summary>
public sealed class Schedule
{
  private readonly string _json;

  private Schedule(string json, DateTimeOffset? at, DateTimeOffset? start, string? rrule, string? tz)
  {
    _json = json;
    At = at;
    Start = start;
    Rrule = rrule;
    Tz = tz;
  }

  public DateTimeOffset? At { get; }
  public DateTimeOffset? Start { get; }
  public string? Rrule { get; }
  public string? Tz { get; }

  /// <summary>A spec with 'at' is one-shot even when rrule fields are also present ('at' wins in NextOnOrAfter/NextAfter).</summary>
  public bool IsRecurring => At is null && Start is not null && Rrule is not null;

  /// <summary>Null when the JSON is not an object or a date field doesn't parse — the grammar is unmet.</summary>
  public static Schedule? Parse(string json)
  {
    try
    {
      using var doc = JsonDocument.Parse(json);
      var root = doc.RootElement;
      if (root.ValueKind != JsonValueKind.Object)
      {
        return null;
      }

      return new Schedule(json, Time(root, "at"), Time(root, "start"), Str(root, "rrule"), Str(root, "tz"));
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return null;
    }
  }

  public static Schedule OneShotAt(DateTimeOffset at)
  {
    var utc = at.ToUniversalTime();
    return new Schedule(new JsonObject { ["at"] = utc.ToString("o") }.ToJsonString(), utc, null, null, null);
  }

  public static Schedule Recurring(DateTimeOffset start, string rrule, string? tz = null)
  {
    var utc = start.ToUniversalTime();
    var node = new JsonObject { ["start"] = utc.ToString("o"), ["rrule"] = rrule };
    if (tz is not null)
    {
      node["tz"] = tz;
    }

    return new Schedule(node.ToJsonString(), null, utc, rrule, tz);
  }

  /// <summary>A schedule with the default tz stamped onto a recurring spec that omits one; otherwise this instance.</summary>
  public Schedule WithDefaultTz(string defaultTz)
  {
    if (Rrule is null || !string.IsNullOrEmpty(Tz))
    {
      return this; // one-shot or already zoned
    }

    var node = JsonNode.Parse(_json)!.AsObject();
    node["tz"] = defaultTz;
    return new Schedule(node.ToJsonString(), At, Start, Rrule, defaultTz);
  }

  /// <summary>
  /// The provably-invalid checks, independent of the clock: grammar, rrule syntax, and the
  /// sub-daily+BY-parts+DST-tz combination the calculator refuses. Deliberately does NOT
  /// reject elapsed one-shots or exhausted recurrences — that distinction is the caller's,
  /// via NextOnOrAfter (see TriggerRepository).
  /// </summary>
  public bool TryValidate(out string? error)
  {
    if (At is null && !IsRecurring)
    {
      error = "Schedule must be {\"at\":\"<iso utc>\"} (one-shot) or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\"} (recurring).";
      return false;
    }

    if (IsRecurring)
    {
      try
      {
        _ = new RecurrencePattern(Rrule!);
      }
      catch (Exception ex) when (ex is ArgumentException or FormatException)
      {
        error = $"Invalid rrule '{Rrule}': {ex.Message}";
        return false;
      }
    }

    // Keyed on Rrule presence (not IsRecurring) to match the old HasUnsupportedSubDailyRule:
    // set_trigger has always rejected this combination even alongside an 'at'.
    if (Rrule is { } rrule && RecurrenceCalculator.IsUnsupportedSubDaily(rrule, Tz))
    {
      error = "Sub-daily rules (SECONDLY/MINUTELY/HOURLY) with BY-part filters are not supported in DST timezones; "
        + "use plain INTERVAL form, or FREQ=DAILY with BYHOUR/BYMINUTE for wall-clock times, or pass tz:\"UTC\".";
      return false;
    }

    error = null;
    return true;
  }

  /// <summary>First occurrence at or after <paramref name="now"/>. A one-shot returns its 'at' even when past (immediately due — expiry depends on this). Null means the recurrence is exhausted (or the spec resolves to nothing).</summary>
  public DateTimeOffset? NextOnOrAfter(DateTimeOffset now)
  {
    if (At is { } at)
    {
      return at;
    }

    if (Start is { } start && Rrule is { } rrule)
    {
      var anchor = start > now ? start : now;
      return RecurrenceCalculator.NextOccurrenceOnOrAfter(start, rrule, anchor, Tz);
    }

    return null;
  }

  public DateTimeOffset? NextAfter(DateTimeOffset firedOccurrence)
  {
    return At is not null
      ? null
      : Start is { } start && Rrule is { } rrule
        ? RecurrenceCalculator.NextOccurrenceAfter(start, rrule, firedOccurrence, Tz)
        : null;
  }

  public string ToJson()
  {
    return _json;
  }

  private static DateTimeOffset? Time(JsonElement root, string name)
  {
    return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
      ? DateTimeOffset.Parse(v.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)
      : null;
  }

  private static string? Str(JsonElement root, string name)
  {
    return root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
  }
}
```

- [ ] `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --filter ScheduleTests` — green (if `Validate_rejects_spec_with_neither...` fails because `Parsed("{}")` — note `Parse("{}")` must return a Schedule with all-null fields, not null: only non-object/undated JSON is unparseable; adjust nothing, the code above already does this).
- [ ] Run the full tietue suite — no regressions (nothing existing references `Schedule` yet); count ≈ 325 + 23.
- [ ] `dotnet format` both csproj + `--verify-no-changes`; commit `feat(tietue): Schedule value type owns the trigger schedule grammar`.

---

## Task 2: Validation moves into `TriggerRepository`; every writer migrates; `Schedules` dies

**Files**
- Modify: `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs`, `Scheduling/SchedulerTick.cs`, `Tools/SetTriggerTool.cs`, `Tools/UpdateTriggerTool.cs`, `Tools/ActivateTool.cs`, `Provisioning/ExpiryReconciler.cs`, `Provisioning/TriggerProvisioner.cs`
- Delete: `src/toimi.tools.tietue/Scheduling/Schedules.cs`, `src/toimi.tools.tietue.Tests/SchedulesTests.cs`
- Tests modified: `UpdateTriggerToolTests.cs` (regression tests FIRST), `TriggerRepositoryTests.cs`, `SetTriggerToolTests.cs`, `TriggerToolsTests.cs`, `ExpiryReconcilerTests.cs`, `TriggerProvisionerTests.cs`, `JobEndToEndTests.cs`

**Interfaces**
- `TriggerRepository.CreateAsync(Guid, Schedule, string, string?, DateTimeOffset, string? source = null, CancellationToken)` — new typed overload; the existing string overload delegates through `ParseOrThrow`. Both throw `TietueValidationException` on invalid or exhausted schedules; persisted triggers are always `Enabled=true` with non-null `NextFireAt`.
- `TriggerRepository.UpdateAsync` — same string signature, now parse/validate/throw before mutating.
- `SetTriggerTool` ctor loses `ToimiConfiguration`; `UpdateTriggerTool` catches `TietueValidationException`.

**Steps**

- [ ] **TDD the gap with teeth first.** Add to `src/toimi.tools.tietue.Tests/UpdateTriggerToolTests.cs` (red — today `UpdateTrigger` returns a success-shaped JSON with `enabled:false`):

```csharp
  [Fact]
  public async Task Subdaily_dst_schedule_is_rejected_not_silently_disabled()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-01-01T09:00:00Z"}""", "notify", null, Past);
    var scheduledFire = t.NextFireAt;

    // set_trigger has always rejected this schedule; update_trigger used to stamp it,
    // get NextFireAt=null, and persist Enabled=false behind a success-shaped response.
    var response = await tool.UpdateTrigger(t.Id.ToString(),
      schedule: /*lang=json,strict*/ """{"start":"2026-01-01T00:00:00Z","rrule":"FREQ=MINUTELY;INTERVAL=30;BYHOUR=9","tz":"Europe/Helsinki"}""");

    Assert.Contains("not supported in DST timezones", response);
    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.True(updated.Enabled);
    Assert.Equal(scheduledFire, updated.NextFireAt);
    Assert.Contains("2027-01-01", updated.Schedule);
  }

  [Fact]
  public async Task Unparseable_schedule_is_rejected_with_message()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-01-01T09:00:00Z"}""", "notify", null, Past);

    var response = await tool.UpdateTrigger(t.Id.ToString(), schedule: "not json");

    Assert.Contains("Invalid schedule JSON", response);
    var updated = (await triggers.ListByEntityAsync(entityId))[0];
    Assert.True(updated.Enabled);
    Assert.Contains("2027-01-01", updated.Schedule);
  }

  [Fact]
  public async Task Exhausted_recurrence_is_rejected()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-01-01T09:00:00Z"}""", "notify", null, Past);

    var response = await tool.UpdateTrigger(t.Id.ToString(),
      schedule: /*lang=json,strict*/ """{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}""");

    Assert.Contains("does not resolve to a future fire time", response);
    Assert.True(((await triggers.ListByEntityAsync(entityId))[0]).Enabled);
  }
```

- [ ] Run `--filter UpdateTriggerToolTests` — confirm the three new tests are RED (and see exactly how: success JSON instead of error text).
- [ ] Rewrite `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Scheduling;

public class TriggerRepository(TietueDbContext db, Toimi.Core.Configuration.ToimiConfiguration config)
{
  internal const string InvalidScheduleJsonError =
    "Invalid schedule JSON. Expected {\"at\":\"<iso utc>\"} for one-shot or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\"} for recurring.";
  internal const string NeverFiresError =
    "Schedule does not resolve to a future fire time. Check the 'at'/'start'+'rrule' fields.";

  public Task<Trigger> CreateAsync(Guid entityId, string scheduleJson, string handlerKind, string? handlerConfig, DateTimeOffset now, string? source = null, CancellationToken ct = default)
  {
    return CreateAsync(entityId, ParseOrThrow(scheduleJson), handlerKind, handlerConfig, now, source, ct);
  }

  public async Task<Trigger> CreateAsync(Guid entityId, Schedule schedule, string handlerKind, string? handlerConfig, DateTimeOffset now, string? source = null, CancellationToken ct = default)
  {
    var (stamped, nextFireAt) = ResolveOrThrow(schedule, now);
    var trigger = new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = stamped.ToJson(),
      HandlerKind = handlerKind,
      HandlerConfig = handlerConfig,
      Source = source,
      Enabled = true,
      NextFireAt = nextFireAt,
      CreatedAt = now,
      UpdatedAt = now,
    };
    db.Triggers.Add(trigger);
    await db.SaveChangesAsync(ct);
    return trigger;
  }

  public Task<Trigger?> GetAsync(Guid id, CancellationToken ct = default)
  {
    return db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
  }

  public async Task<IReadOnlyList<Trigger>> ListByEntityAsync(Guid entityId, CancellationToken ct = default)
  {
    return await db.Triggers.Where(t => t.EntityId == entityId).OrderBy(t => t.CreatedAt).ToListAsync(ct);
  }

  public async Task<Trigger?> UpdateAsync(Guid id, string? scheduleJson, string? handlerConfig, bool? enabled, DateTimeOffset now, CancellationToken ct = default)
  {
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (trigger is null)
    {
      return null;
    }

    // Validate and resolve BEFORE mutating the tracked row: a validation throw must not leave
    // half-applied changes for a later SaveChangesAsync in the same scope to sweep up.
    if (scheduleJson is not null)
    {
      var (stamped, nextFireAt) = ResolveOrThrow(ParseOrThrow(scheduleJson), now);
      trigger.Schedule = stamped.ToJson();
      trigger.NextFireAt = nextFireAt;
    }

    if (handlerConfig is not null)
    {
      trigger.HandlerConfig = handlerConfig;
    }

    if (enabled is not null)
    {
      trigger.Enabled = enabled.Value;
    }

    // Re-enabling an exhausted trigger must not produce Enabled=true with a null
    // NextFireAt — such a trigger is invisible to the scheduler's due query forever.
    // Recompute from the schedule; a one-shot 'at' in the past resolves to a non-null
    // but already-elapsed instant (NextOnOrAfter does not compare 'at' to 'now'),
    // so also require the recomputed fire time to still be in the future before
    // allowing the re-enable; otherwise refuse it and leave NextFireAt null.
    if (trigger.Enabled && trigger.NextFireAt is null)
    {
      var recomputed = Schedule.Parse(trigger.Schedule)?.NextOnOrAfter(now);
      trigger.NextFireAt = recomputed is not null && recomputed > now ? recomputed : null;
      trigger.Enabled = trigger.NextFireAt is not null;
    }

    trigger.UpdatedAt = now;
    await db.SaveChangesAsync(ct);
    return trigger;
  }

  public async Task<bool> DeleteAsync(Guid id, CancellationToken ct = default)
  {
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (trigger is null)
    {
      return false;
    }

    db.Triggers.Remove(trigger);
    await db.SaveChangesAsync(ct);
    return true;
  }

  private static Schedule ParseOrThrow(string scheduleJson)
  {
    return Schedule.Parse(scheduleJson) ?? throw new TietueValidationException([InvalidScheduleJsonError]);
  }

  // Stamp the user's default tz (so the persisted schedule is self-describing and its
  // wall-clock survives DST) → validate → resolve the first fire. Throwing — not silently
  // disabling — is the contract: every persisted trigger is born enabled with a real
  // NextFireAt. "Invalid" (grammar/rrule/sub-daily) and "exhausted" (valid but no future
  // occurrence) get distinct messages.
  private (Schedule Schedule, DateTimeOffset NextFireAt) ResolveOrThrow(Schedule schedule, DateTimeOffset now)
  {
    var stamped = schedule.WithDefaultTz(config.UserTimeZone);
    if (!stamped.TryValidate(out var error))
    {
      throw new TietueValidationException([error!]);
    }

    var nextFireAt = stamped.NextOnOrAfter(now) ?? throw new TietueValidationException([NeverFiresError]);
    return (stamped, nextFireAt);
  }
}
```

- [ ] `src/toimi.tools.tietue/Tools/SetTriggerTool.cs` — delete the duplicated pre-validation block (:36-49) and the `ToimiConfiguration` ctor param; wrap the create in the `DefineTypeTool`-style catch. Full replacement body:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SetTriggerTool(TriggerRepository repository, TietueDbContext db, HandlerRegistry handlers)
{
  [McpServerTool, Description("Schedule a trigger on an entity. 'schedule' is JSON: {\"at\":\"<iso utc>\"} for one-shot, or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\",\"tz\":\"Europe/Helsinki\"} for recurring (RFC 5545); recurring schedules without a tz default to the server's user timezone, pass \"tz\":\"UTC\" for fixed-UTC recurrence. 'handlerKind' is one of: notify, set-field, delete, script, message; 'handlerConfig' is its JSON config.")]
  public async Task<string> SetTrigger(
      [Description("Entity id (GUID)")] string entityId,
      [Description("Schedule spec JSON")] string schedule,
      [Description("Handler kind: one of: notify, set-field, delete, script, message")] string handlerKind,
      [Description("Handler config JSON (optional)")] string? handlerConfig = null)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    if (!await db.Entities.AnyAsync(e => e.Id == id))
    {
      return $"No entity found with id {id}.";
    }

    if (handlers.Resolve(handlerKind) is null)
    {
      return $"Unknown handlerKind '{handlerKind}'. Valid kinds: {string.Join(", ", handlers.Kinds)}.";
    }

    try
    {
      // Stamping + schedule validation live in the repository — the single choke point
      // every trigger-writing path goes through.
      var t = await repository.CreateAsync(id, schedule, handlerKind, handlerConfig, DateTimeOffset.UtcNow);
      return JsonSerializer.Serialize(new { id = t.Id.ToString(), nextFireAt = t.NextFireAt?.ToString("o") });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
```

- [ ] `src/toimi.tools.tietue/Tools/UpdateTriggerTool.cs` — wrap `UpdateAsync` in the same catch (this + the repository change IS the regression fix):

```csharp
    try
    {
      var t = await repository.UpdateAsync(triggerId, schedule, handlerConfig, enabled, DateTimeOffset.UtcNow);
      return t is null
        ? $"Trigger '{id}' not found."
        : JsonSerializer.Serialize(new { id = t.Id.ToString(), enabled = t.Enabled, nextFireAt = t.NextFireAt?.ToString("o") });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
```

(add `using toimi.tools.tietue.Validation;`)

- [ ] `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs` line 53: `trigger.NextFireAt = Schedule.Parse(trigger.Schedule)?.NextAfter(occurrence);` (stored schedules were validated at write; the null-conditional is defense for pre-refactor rows).
- [ ] `src/toimi.tools.tietue/Tools/ActivateTool.cs` — replace the hand-built JSON (:33-35):

```csharp
      var config = new JsonObject { ["promptTemplate"] = message }.ToJsonString();
      // OneShotAt is valid by construction and a one-shot always resolves (a past 'at' is
      // immediately due), so CreateAsync cannot throw here.
      var t = await triggers.CreateAsync(id, Schedule.OneShotAt(at), "message", config, DateTimeOffset.UtcNow);
```

- [ ] `src/toimi.tools.tietue/Provisioning/ExpiryReconciler.cs` — `ExpiryAt` returns `DateTimeOffset?`; build via the factory:

```csharp
    var at = ExpiryAt(entity.Data, cfg.Field);
    if (at is null)
    {
      return; // field absent OR not a parseable date — a garbage date must not arm a dead trigger
    }

    var kind = cfg.Prompt is null ? "delete" : "message";
    var handlerConfig = cfg.Prompt is null ? null : MessageConfig(entity.Type, cfg.Field, cfg.Prompt);

    await triggers.CreateAsync(entity.Id, Schedule.OneShotAt(at.Value), kind, handlerConfig, now, SourceTag, ct);
```

```csharp
  private static DateTimeOffset? ExpiryAt(JsonDocument data, string field)
  {
    return data.RootElement.TryGetProperty(field, out var v)
      && v.ValueKind == JsonValueKind.String
      && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
        ? at
        : null;
  }
```

- [ ] `src/toimi.tools.tietue/Provisioning/TriggerProvisioner.cs` — `BuildSchedule` returns `Schedule?`; `ProvisionAsync` catches and logs data-dependent rejections:

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Provisioning;

public class TriggerProvisioner(TriggerRepository triggers, ILogger<TriggerProvisioner>? logger = null)
{
  private readonly ILogger<TriggerProvisioner> _logger = logger ?? NullLogger<TriggerProvisioner>.Instance;

  public async Task ProvisionAsync(Entity entity, string? defaultTriggersJson, DateTimeOffset now, CancellationToken ct = default)
  {
    if (string.IsNullOrWhiteSpace(defaultTriggersJson))
    {
      return;
    }

    JsonNode? templates;
    try
    {
      templates = JsonNode.Parse(defaultTriggersJson);
    }
    catch (JsonException)
    {
      return;
    }

    if (templates is not JsonArray arr)
    {
      return;
    }

    foreach (var template in arr.OfType<JsonObject>())
    {
      var schedule = BuildSchedule(template["when"]?.AsObject(), entity.Data);
      if (schedule is null)
      {
        continue; // no (parseable) atField value on this entity — by design, no trigger
      }

      var handler = template["handler"]?.AsObject();
      var kind = handler?["kind"]?.GetValue<string>();
      if (string.IsNullOrEmpty(kind))
      {
        continue;
      }

      var config = handler?["config"]?.ToJsonString();
      try
      {
        await triggers.CreateAsync(entity.Id, schedule, kind, config, now, ct: ct);
      }
      catch (TietueValidationException ex)
      {
        // Entity data produced an invalid or already-exhausted schedule (e.g. a spent COUNT
        // rrule). The entity create must survive; the skip is logged, not silent.
        _logger.LogWarning("Skipped default '{Kind}' trigger for entity {EntityId} ({Type}): {Errors}",
          kind, entity.Id, entity.Type, string.Join("; ", ex.Errors));
      }
    }
  }

  private static Schedule? BuildSchedule(JsonObject? when, JsonDocument data)
  {
    if (when is null)
    {
      return null;
    }

    var atField = when["atField"]?.GetValue<string>();
    if (atField is null || !data.RootElement.TryGetProperty(atField, out var atVal) || atVal.ValueKind != JsonValueKind.String)
    {
      return null;
    }

    if (!DateTimeOffset.TryParse(atVal.GetString(), CultureInfo.InvariantCulture,
      DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
    {
      return null; // garbage date in the entity's field — no trigger (was: a disabled zombie row)
    }

    var rruleField = when["rruleField"]?.GetValue<string>();
    var rrule = rruleField is not null && data.RootElement.TryGetProperty(rruleField, out var rr)
      && rr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rr.GetString())
        ? rr.GetString()
        : null;
    if (rrule is null)
    {
      return Schedule.OneShotAt(at);
    }

    var tzField = when["tzField"]?.GetValue<string>();
    var tz = tzField is not null && data.RootElement.TryGetProperty(tzField, out var tzv) && tzv.ValueKind == JsonValueKind.String
      ? tzv.GetString()
      : null;
    return Schedule.Recurring(at, rrule, tz);
  }
}
```

- [ ] Delete `src/toimi.tools.tietue/Scheduling/Schedules.cs` and `src/toimi.tools.tietue.Tests/SchedulesTests.cs`. Build — any remaining `Schedules.` reference is a straggler to migrate.
- [ ] Update the affected tests:
  - `TriggerRepositoryTests.cs`: replace `Create_with_unresolvable_schedule_yields_a_disabled_trigger` with throws-tests (add `using toimi.tools.tietue.Validation;`):

```csharp
  [Fact]
  public async Task Create_with_unparseable_schedule_throws()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"at":"soon"}""", "notify", null, Now));

    Assert.Contains("Invalid schedule JSON", ex.Message);
    Assert.Empty(db.Triggers);
  }

  [Fact]
  public async Task Create_with_exhausted_recurrence_throws_never_fires()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"start":"2020-01-01T00:00:00Z","rrule":"FREQ=YEARLY;COUNT=1"}""", "notify", null, Now));

    Assert.Contains("does not resolve to a future fire time", ex.Message);
  }

  [Fact]
  public async Task Create_with_garbage_rrule_throws_instead_of_crashing()
  {
    // Regression: garbage rrule used to escape as an unhandled Ical.Net exception from
    // InitialNextFireAt. Whether TryValidate's RecurrencePattern parse or the never-fires
    // check catches it, the write must fail with a TietueValidationException.
    using var db = TestDb.New();
    var repo = new TriggerRepository(db, TestConfig.Default);

    await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.CreateAsync(Guid.NewGuid(), /*lang=json,strict*/ """{"start":"2026-06-01T09:00:00Z","rrule":"NOT-AN-RRULE"}""", "notify", null, Now));
    Assert.Empty(db.Triggers);
  }
```

  - `SetTriggerToolTests.cs` / `TriggerToolsTests.cs`: drop the trailing `TestConfig.Default` from all 9 `new SetTriggerTool(...)` constructions; change `Rejects_malformed_schedule`'s assertion to `Assert.Contains("Invalid schedule JSON", result);`. All other message assertions (`"does not resolve to a future fire time"`, `"not supported in DST timezones"`, `tz:"UTC"`) pass unchanged — the texts moved verbatim.
  - `ExpiryReconcilerTests.cs`: strengthen `Garbage_expiry_date_does_not_arm_a_zombie_trigger` — replace the two asserts with `Assert.Null(t);` (garbage now provisions nothing instead of a disabled row). `Past_expiry_date_arms_an_immediately_due_trigger` must pass UNCHANGED — it proves elapsed one-shots aren't over-rejected.
  - `TriggerProvisionerTests.cs`: add two facts:

```csharp
  [Fact]
  public async Task Garbage_due_date_provisions_no_trigger()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"x","dueAt":"whenever"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, DateTimeOffset.UtcNow);

    Assert.Empty(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
  }

  [Fact]
  public async Task Exhausted_recurrence_from_entity_data_is_skipped_not_thrown()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db, TestConfig.Default));
    var e = Reminder(/*lang=json,strict*/ """{"title":"Old","dueAt":"2020-01-01T09:00:00Z","rrule":"FREQ=DAILY;COUNT=1"}""");

    // The provision (running inside entity create in prod) must swallow the repository's
    // rejection: the entity survives, the dead template is skipped.
    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    Assert.Empty(await new TriggerRepository(db, TestConfig.Default).ListByEntityAsync(e.Id));
  }
```

  - `JobEndToEndTests.cs:45`: `Assert.Contains("2030-01-01T06:00:00Z", trigger.Schedule)` → `Assert.Contains("2030-01-01T06:00:00", trigger.Schedule)` (provisioner-built schedules now serialize `start` in round-trip `"o"` format — same instant).
- [ ] Full tietue suite green (expect stragglers only in files above; fix any additional `Contains(...)` on stored schedule strings the run surfaces). Count must be ≥ 325 (−14 SchedulesTests, +6 new here, +23 from Task 1).
- [ ] `dotnet format` both csproj + `--verify-no-changes`; commit `refactor(tietue): trigger writes validate schedules in TriggerRepository; update_trigger no longer silently disables`.

---

## Task 3: `INativeHandler.ValidateConfig` + per-handler implementations (TDD)

**Files**
- Modify: `src/toimi.tools.tietue/Handlers/INativeHandler.cs`, `Handlers/NotifyHandler.cs`, `Handlers/MessageHandler.cs`, `Handlers/SetFieldHandler.cs`, `Handlers/ScriptHandler.cs`
- Create: `src/toimi.tools.tietue/Handlers/ConfigValidation.cs`
- Tests modified: `NotifyHandlerTests.cs`, `MessageHandlerTests.cs`, `SetFieldHandlerTests.cs`, `ScriptHandlerTests.cs`, `DeleteHandlerTests.cs`
- NOT touched: `DeleteHandler.cs` (the DIM default IS its honest answer), all 5 test stub handlers.

**Interfaces**
- `INativeHandler` gains `ValidationResult ValidateConfig(string? configJson)` with a default body returning `ValidationResult.Valid()` (block body — IDE0022 applies to DIMs).
- `internal static class ConfigValidation` — shared require-object parsing so the four overrides don't quadruplicate JSON boilerplate.

**Steps**

- [ ] Write the failing tests (red: `ValidateConfig` doesn't exist). Add to each handler's existing test file:

```csharp
  // NotifyHandlerTests.cs
  [Theory]
  [InlineData(null)]
  [InlineData(/*lang=json,strict*/ """{"tags":"bell"}""")]
  [InlineData(/*lang=json,strict*/ """{"titleTemplate":""}""")]
  [InlineData("not json")]
  [InlineData("[]")]
  public void ValidateConfig_rejects_configs_that_send_empty_notifications(string? config)
  {
    var result = new NotifyHandler(new FakeNotifier()).ValidateConfig(config);
    Assert.False(result.IsValid);
  }

  [Fact]
  public void ValidateConfig_rejects_non_string_tags()
  {
    // HandleAsync's Str() silently drops non-strings — a tags array is a swallowed typo.
    var result = new NotifyHandler(new FakeNotifier()).ValidateConfig(/*lang=json,strict*/ """{"titleTemplate":"{title}","tags":["bell"]}""");
    Assert.False(result.IsValid);
    Assert.Contains("tags", result.Errors[0]);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"titleTemplate":"{title}"}""")]
  [InlineData(/*lang=json,strict*/ """{"messageTemplate":"{description}"}""")]
  [InlineData(/*lang=json,strict*/ """{"titleTemplate":"{title}","messageTemplate":"{description}","priority":"high","tags":"bell"}""")]
  public void ValidateConfig_accepts_configs_with_a_template(string config)
  {
    Assert.True(new NotifyHandler(new FakeNotifier()).ValidateConfig(config).IsValid);
  }
```

```csharp
  // MessageHandlerTests.cs
  [Theory]
  [InlineData(null)]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ """{"promptTemplate":""}""")]
  [InlineData(/*lang=json,strict*/ """{"promptTempalte":"typo'd key"}""")]
  public void ValidateConfig_rejects_configs_that_run_an_empty_prompt(string? config)
  {
    var result = new MessageHandler(new FakeAgentRunner()).ValidateConfig(config);
    Assert.False(result.IsValid);
    Assert.Contains("promptTemplate", result.Errors[0]);
  }

  [Fact]
  public void ValidateConfig_accepts_a_prompt_template()
  {
    Assert.True(new MessageHandler(new FakeAgentRunner())
      .ValidateConfig(/*lang=json,strict*/ """{"promptTemplate":"{prompt}"}""").IsValid);
  }
```

```csharp
  // SetFieldHandlerTests.cs  (construct the handler the way existing facts in the file do)
  [Theory]
  [InlineData(null)]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ """{"value":1}""")]
  public void ValidateConfig_rejects_configs_without_a_path(string? config)
  {
    Assert.False(Handler().ValidateConfig(config).IsValid);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"path":"status","value":"done"}""")]
  [InlineData(/*lang=json,strict*/ """{"path":"status"}""")]
  public void ValidateConfig_accepts_a_path_with_or_without_value(string config)
  {
    // A missing value is a genuine null-assignment, not an error.
    Assert.True(Handler().ValidateConfig(config).IsValid);
  }
```

(if `SetFieldHandlerTests` has no `Handler()` helper, inline `new SetFieldHandler(new EntityRepository(db, new SchemaValidator()))` with a `TestDb.New()` per the file's existing style)

```csharp
  // ScriptHandlerTests.cs  (reuse the file's existing handler-construction helper/pattern)
  [Theory]
  [InlineData(null)]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ """{"fromEntity":false}""")]
  [InlineData(/*lang=json,strict*/ """{"source":""}""")]
  public void ValidateConfig_rejects_configs_with_nothing_to_execute(string? config)
  {
    Assert.False(Handler().ValidateConfig(config).IsValid);
  }

  [Fact]
  public void ValidateConfig_rejects_non_array_hosts_and_capabilities()
  {
    // StrArray silently coerces wrong shapes to [] — a string-valued allowedHosts becomes
    // a script with no egress.
    var result = Handler().ValidateConfig(/*lang=json,strict*/ """{"source":"export default () => ({})","allowedHosts":"api.example.com","capabilities":[1]}""");
    Assert.False(result.IsValid);
    Assert.Equal(2, result.Errors.Count);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"fromEntity":true}""")]
  [InlineData(/*lang=json,strict*/ """{"source":"export default () => ({})"}""")]
  [InlineData(/*lang=json,strict*/ """{"source":"export default () => ({})","allowedHosts":["api.example.com"],"capabilities":["setField"]}""")]
  public void ValidateConfig_accepts_runnable_configs(string config)
  {
    Assert.True(Handler().ValidateConfig(config).IsValid);
  }
```

```csharp
  // DeleteHandlerTests.cs
  [Theory]
  [InlineData(null)]
  [InlineData("anything, even garbage")]
  public void ValidateConfig_accepts_anything_config_is_never_read(string? config)
  {
    Assert.True(Handler().ValidateConfig(config).IsValid);
  }
```

- [ ] Run the handler test filters — red (compile failure on `ValidateConfig`).
- [ ] `src/toimi.tools.tietue/Handlers/INativeHandler.cs`:

```csharp
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

public interface INativeHandler
{
  string Kind { get; }

  Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default);

  /// <summary>
  /// Whether HandleAsync could do useful work with this trigger config. Called only by
  /// trigger-WRITING paths (set_trigger, update_trigger, define_type) — never by the
  /// scheduler or run_trigger, which fire whatever exists. Default: any config is fine.
  /// </summary>
  ValidationResult ValidateConfig(string? configJson)
  {
    return ValidationResult.Valid();
  }
}
```

- [ ] Create `src/toimi.tools.tietue/Handlers/ConfigValidation.cs`:

```csharp
using System.Text.Json;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

/// <summary>Shared write-time parsing for ValidateConfig implementations.</summary>
internal static class ConfigValidation
{
  /// <summary>
  /// Parses a required JSON-object config. Returns null with <paramref name="failure"/> set
  /// when the config is absent (reported as <paramref name="requirement"/>), malformed JSON,
  /// or not an object. The caller owns disposing a non-null result.
  /// </summary>
  public static JsonDocument? RequireObject(string? configJson, string requirement, out ValidationResult? failure)
  {
    if (configJson is null)
    {
      failure = ValidationResult.Invalid(requirement);
      return null;
    }

    JsonDocument doc;
    try
    {
      doc = JsonDocument.Parse(configJson);
    }
    catch (JsonException ex)
    {
      failure = ValidationResult.Invalid($"Config is not valid JSON: {ex.Message}");
      return null;
    }

    if (doc.RootElement.ValueKind != JsonValueKind.Object)
    {
      doc.Dispose();
      failure = ValidationResult.Invalid("Config must be a JSON object.");
      return null;
    }

    failure = null;
    return doc;
  }
}
```

- [ ] Add the four overrides (each file adds `using toimi.tools.tietue.Validation;`):

`NotifyHandler.cs`:

```csharp
  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "notify config requires 'titleTemplate' and/or 'messageTemplate' as a non-empty string — without one, every fire sends an empty notification.";
    using var cfg = ConfigValidation.RequireObject(configJson, Requirement, out var failure);
    if (cfg is null)
    {
      return failure!;
    }

    var errors = new List<string>();
    if (string.IsNullOrEmpty(Str(cfg.RootElement, "titleTemplate")) && string.IsNullOrEmpty(Str(cfg.RootElement, "messageTemplate")))
    {
      errors.Add(Requirement);
    }

    foreach (var name in (string[])["titleTemplate", "messageTemplate", "priority", "tags"])
    {
      if (cfg.RootElement.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.String)
      {
        errors.Add($"notify config '{name}' must be a string.");
      }
    }

    return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors);
  }
```

`MessageHandler.cs`:

```csharp
  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "message config requires 'promptTemplate' as a non-empty string — without it the agent runs with an empty prompt.";
    using var cfg = ConfigValidation.RequireObject(configJson, Requirement, out var failure);
    if (cfg is null)
    {
      return failure!;
    }

    return cfg.RootElement.TryGetProperty("promptTemplate", out var p)
      && p.ValueKind == JsonValueKind.String
      && !string.IsNullOrWhiteSpace(p.GetString())
        ? ValidationResult.Valid()
        : ValidationResult.Invalid(Requirement);
  }
```

`SetFieldHandler.cs`:

```csharp
  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "set-field config requires 'path' as a non-empty string — without it the handler skips every fire.";
    using var cfg = ConfigValidation.RequireObject(configJson, Requirement, out var failure);
    if (cfg is null)
    {
      return failure!;
    }

    return cfg.RootElement.TryGetProperty("path", out var p)
      && p.ValueKind == JsonValueKind.String
      && !string.IsNullOrEmpty(p.GetString())
        ? ValidationResult.Valid()
        : ValidationResult.Invalid(Requirement);
  }
```

`ScriptHandler.cs` (its private `Str` already exists; note the config key for grants is `capabilities`):

```csharp
  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "script config requires 'source' as a non-empty string, or 'fromEntity': true to run the job entity's own code.";
    using var cfg = ConfigValidation.RequireObject(configJson, Requirement, out var failure);
    if (cfg is null)
    {
      return failure!;
    }

    var root = cfg.RootElement;
    if (root.TryGetProperty("fromEntity", out var fe) && fe.ValueKind == JsonValueKind.True)
    {
      return ValidationResult.Valid(); // the job entity is authoritative; inline fields are ignored
    }

    var errors = new List<string>();
    if (string.IsNullOrWhiteSpace(Str(root, "source")))
    {
      errors.Add(Requirement);
    }

    foreach (var name in (string[])["allowedHosts", "capabilities"])
    {
      if (root.TryGetProperty(name, out var v)
        && (v.ValueKind != JsonValueKind.Array || v.EnumerateArray().Any(i => i.ValueKind != JsonValueKind.String)))
      {
        errors.Add($"script config '{name}' must be an array of strings.");
      }
    }

    return errors.Count == 0 ? ValidationResult.Valid() : ValidationResult.Invalid(errors);
  }
```

- [ ] Run handler test filters green, then the full tietue suite (the stub handlers in `RunTriggerToolTests`/`SchedulerTickTests`/`OccurrenceRunnerTests` must compile untouched — that's the DIM working).
- [ ] `dotnet format` both csproj + `--verify-no-changes` (watch IDE0022 on the DIM); commit `feat(tietue): handlers declare what configs they can run via ValidateConfig`.

---

## Task 4: Enforce `ValidateConfig` at `set_trigger` / `update_trigger` (TDD)

**Files**
- Modify: `src/toimi.tools.tietue/Tools/SetTriggerTool.cs`, `Tools/UpdateTriggerTool.cs`
- Tests modified: `SetTriggerToolTests.cs`, `UpdateTriggerToolTests.cs`

**Interfaces**
- `UpdateTriggerTool` ctor becomes `(TriggerRepository repository, HandlerRegistry handlers)` — DI resolves it; test construction sites updated.

**Steps**

- [ ] Write the failing tests (red — configs currently pass straight through):

```csharp
  // SetTriggerToolTests.cs
  [Fact]
  public async Task Rejects_config_the_handler_cannot_run()
  {
    using var db = TestDb.New();
    var e = await SeedEntityAsync(db);
    var tool = new SetTriggerTool(new TriggerRepository(db, TestConfig.Default), db, Handlers());

    // notify with no template: would fire an empty notification forever.
    var result = await tool.SetTrigger(e.Id.ToString(), /*lang=json,strict*/ """{"at":"2026-06-20T09:00:00Z"}""", "notify", /*lang=json,strict*/ """{"tags":"bell"}""");

    Assert.Contains("titleTemplate", result);
    Assert.Empty(await db.Triggers.ToListAsync());
  }
```

```csharp
  // UpdateTriggerToolTests.cs
  [Fact]
  public async Task Rejects_config_the_triggers_handler_cannot_run()
  {
    var (db, triggers, tool, entityId) = await SetupAsync();
    using var _1 = db;
    var t = await triggers.CreateAsync(entityId, /*lang=json,strict*/ """{"at":"2027-01-01T09:00:00Z"}""", "notify",
      /*lang=json,strict*/ """{"titleTemplate":"keep"}""", Past);

    var response = await tool.UpdateTrigger(t.Id.ToString(), handlerConfig: /*lang=json,strict*/ """{"priority":"high"}""");

    Assert.Contains("titleTemplate", response);
    Assert.Equal(/*lang=json,strict*/ """{"titleTemplate":"keep"}""", (await triggers.ListByEntityAsync(entityId))[0].HandlerConfig);
  }
```

- [ ] `SetTriggerTool.SetTrigger`: replace the null-only kind check with resolution + config validation (between the entity check and the try/create):

```csharp
    if (handlers.Resolve(handlerKind) is not { } handler)
    {
      return $"Unknown handlerKind '{handlerKind}'. Valid kinds: {string.Join(", ", handlers.Kinds)}.";
    }

    var configCheck = handler.ValidateConfig(handlerConfig);
    if (!configCheck.IsValid)
    {
      return string.Join("; ", configCheck.Errors);
    }
```

- [ ] `UpdateTriggerTool`: ctor `(TriggerRepository repository, HandlerRegistry handlers)` (add `using toimi.tools.tietue.Handlers;`); before the try/update block:

```csharp
    if (handlerConfig is not null)
    {
      var existing = await repository.GetAsync(triggerId);
      if (existing is null)
      {
        return $"Trigger '{id}' not found.";
      }

      // A legacy trigger whose kind no longer resolves can't be config-validated; leave it
      // to run_trigger's unknown-kind error path rather than blocking edits.
      if (handlers.Resolve(existing.HandlerKind) is { } handler)
      {
        var configCheck = handler.ValidateConfig(handlerConfig);
        if (!configCheck.IsValid)
        {
          return string.Join("; ", configCheck.Errors);
        }
      }
    }
```

- [ ] Update existing tests that now trip config validation:
  - `UpdateTriggerToolTests.SetupAsync`: `new UpdateTriggerTool(triggers, new HandlerRegistry([new NotifyHandler(new FakeNotifier())]))` (add `using toimi.tools.tietue.Handlers;`).
  - `SetTriggerToolTests`: the call sites that pass kind `"notify"` with a null config but expect a SCHEDULE error or success — `Rejects_schedule_that_never_fires`, both `Rejects_subdaily_by_part_rule...` theory cases, `Accepts_subdaily_by_part_rule_with_utc_tz`, `Rejects_malformed_schedule` — now pass `/*lang=json,strict*/ """{"titleTemplate":"hi"}"""` as the 4th argument so they still exercise the schedule path. (`Rejects_non_guid_entity_id`, `Rejects_unknown_entity`, `Rejects_unknown_handler_kind` fail before config validation — unchanged.)
- [ ] Full tietue suite green.
- [ ] `dotnet format` both csproj + `--verify-no-changes`; commit `feat(tietue): set_trigger/update_trigger reject configs their handlers cannot run`.

---

## Task 5: `define_type`-time validation of DefaultTriggers templates (TDD)

**Files**
- Create: `src/toimi.tools.tietue/Provisioning/TriggerTemplates.cs`
- Modify: `src/toimi.tools.tietue/Types/TypeRepository.cs`
- Tests modified: `TypeRepositoryTests.cs`, `TypeSeederTests.cs`
- NOT touched: `DefineTypeTool.cs` (already catches `TietueValidationException`), `Program.cs` (DI injects the already-registered scoped `HandlerRegistry` into the new optional param).

**Interfaces**
- `public static class TriggerTemplates` with `public static IReadOnlyList<string> Validate(string defaultTriggersJson, HandlerRegistry? handlers)`.
- `TypeRepository` ctor becomes `(TietueDbContext db, HandlerRegistry? handlers = null)` — the 62 bare test constructions compile unchanged and get structure-only validation; prod always has the registry.

**Steps**

- [ ] Write the failing tests. In `TypeRepositoryTests.cs` (add usings for `Handlers`, `Provisioning` as needed):

```csharp
  private static HandlerRegistry NotifyOnly()
  {
    return new HandlerRegistry([new NotifyHandler(new FakeNotifier())]);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"not":"an array"}""", "must be a JSON array")]
  [InlineData(/*lang=json,strict*/ """[{"handler":{"kind":"notify","config":{"titleTemplate":"{t}"}}}]""", "atField")]
  [InlineData(/*lang=json,strict*/ """[{"when":{"atField":""},"handler":{"kind":"notify","config":{"titleTemplate":"{t}"}}}]""", "atField")]
  [InlineData(/*lang=json,strict*/ """[{"when":{"atField":"dueAt"}}]""", "handler.kind")]
  public async Task Define_rejects_structurally_broken_default_triggers(string defaultTriggers, string expectedError)
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db); // structure checks need no registry

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.DefineAsync("broken", /*lang=json,strict*/ """{"type":"object"}""", null, defaultTriggers));

    Assert.Contains(expectedError, ex.Message);
    Assert.Null(await repo.GetAsync("broken"));
  }

  [Fact]
  public async Task Define_with_registry_rejects_unknown_handler_kind()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db, NotifyOnly());

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.DefineAsync("broken", /*lang=json,strict*/ """{"type":"object"}""", null,
        /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"nootify","config":{"titleTemplate":"{t}"}}}]"""));

    Assert.Contains("nootify", ex.Message);
  }

  [Fact]
  public async Task Define_with_registry_rejects_config_the_handler_cannot_run()
  {
    // Finding 4's provisioner tail: a typo'd template used to be stamped onto every new
    // entity and then silently skipped or uselessly fired. Now define_type refuses it.
    using var db = TestDb.New();
    var repo = new TypeRepository(db, NotifyOnly());

    var ex = await Assert.ThrowsAsync<TietueValidationException>(
      () => repo.DefineAsync("broken", /*lang=json,strict*/ """{"type":"object"}""", null,
        /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"notify","config":{"titelTemplate":"{t}"}}}]"""));

    Assert.Contains("titleTemplate", ex.Message);
  }

  [Fact]
  public async Task Define_without_registry_accepts_unknown_kind_structure_only()
  {
    // Null registry (bare test construction) = structural checks only, by design.
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    var t = await repo.DefineAsync("loose", /*lang=json,strict*/ """{"type":"object"}""", null,
      /*lang=json,strict*/ """[{"when":{"atField":"dueAt"},"handler":{"kind":"whatever"}}]""");

    Assert.Equal("loose", t.Name);
  }
```

  In `TypeSeederTests.cs` — the constraint test proving seeded types are valid reference examples under FULL validation (all five real handlers; construct `ScriptHandler` the `JobEndToEndTests` way):

```csharp
  [Fact]
  public async Task Seeded_types_pass_full_default_trigger_validation()
  {
    using var db = TestDb.New();
    var entities = new Entities.EntityRepository(db, new Validation.SchemaValidator());
    var registry = new Handlers.HandlerRegistry(
    [
      new Handlers.NotifyHandler(new FakeNotifier()),
      new Handlers.MessageHandler(new FakeAgentRunner()),
      new Handlers.SetFieldHandler(entities),
      new Handlers.DeleteHandler(entities),
      new Handlers.ScriptHandler(new FakeSuoritinClient(),
        new Scripts.ScriptEffectApplier(entities, new FakeMcpInvoker()),
        new Scripts.RunTokenStore(), new Scripts.ScriptOptions(), new Scripts.SuoritinOptions()),
    ]);
    var repo = new TypeRepository(db, registry);

    // Must not throw: reminder's notify, schedule's message, and job's fromEntity script
    // configs are the reference examples of valid DefaultTriggers.
    await new TypeSeeder(repo).SeedAsync();

    Assert.Equal(5, (await repo.ListAsync()).Count);
  }
```

  (adjust namespace qualifiers/usings to the file's existing style; `SuoritinOptions` lives where `SuoritinClient` declares it — mirror `JobEndToEndTests`)
- [ ] Run `--filter "TypeRepositoryTests|TypeSeederTests"` — red.
- [ ] Create `src/toimi.tools.tietue/Provisioning/TriggerTemplates.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Provisioning;

/// <summary>
/// Structural validation of a type's DefaultTriggers templates at define_type time, over the
/// grammar TriggerProvisioner consumes. when.atField/rruleField/tzField are FIELD REFERENCES
/// resolved per-entity, so only their structure is checked here (schedule CONTENT can only be
/// validated per-entity — TriggerProvisioner logs and skips those). handler.config is literal
/// JSON ({token} placeholders are plain strings), so each handler's ValidateConfig applies
/// directly when a registry is available; without one (bare test repositories) the check is
/// structure-only.
/// </summary>
public static class TriggerTemplates
{
  public static IReadOnlyList<string> Validate(string defaultTriggersJson, HandlerRegistry? handlers)
  {
    JsonNode? root;
    try
    {
      root = JsonNode.Parse(defaultTriggersJson);
    }
    catch (JsonException ex)
    {
      return [$"Invalid default triggers JSON: {ex.Message}"];
    }

    if (root is not JsonArray arr)
    {
      return ["defaultTriggers must be a JSON array of trigger templates."];
    }

    var errors = new List<string>();
    for (var i = 0; i < arr.Count; i++)
    {
      if (arr[i] is not JsonObject template)
      {
        errors.Add($"defaultTriggers[{i}] must be an object.");
        continue;
      }

      ValidateWhen(template, i, errors);
      ValidateHandler(template, i, handlers, errors);
    }

    return errors;
  }

  private static void ValidateWhen(JsonObject template, int i, List<string> errors)
  {
    if (template["when"] is not JsonObject when)
    {
      errors.Add($"defaultTriggers[{i}].when must be an object naming an 'atField'.");
      return;
    }

    if (when["atField"] is not JsonValue at || !at.TryGetValue<string>(out var atField) || string.IsNullOrWhiteSpace(atField))
    {
      errors.Add($"defaultTriggers[{i}].when.atField must name the entity field holding the first fire time.");
    }

    foreach (var name in (string[])["rruleField", "tzField"])
    {
      if (when[name] is { } v && (v is not JsonValue value || !value.TryGetValue<string>(out _)))
      {
        errors.Add($"defaultTriggers[{i}].when.{name} must be a string field name.");
      }
    }
  }

  private static void ValidateHandler(JsonObject template, int i, HandlerRegistry? handlers, List<string> errors)
  {
    if (template["handler"] is not JsonObject handlerNode
      || handlerNode["kind"] is not JsonValue kindValue
      || !kindValue.TryGetValue<string>(out var kind)
      || string.IsNullOrWhiteSpace(kind))
    {
      errors.Add($"defaultTriggers[{i}].handler.kind must be a handler kind string.");
      return;
    }

    if (handlers is null)
    {
      return; // structure-only context (no registry): kind/config checked at write time instead
    }

    if (handlers.Resolve(kind) is not { } handler)
    {
      errors.Add($"defaultTriggers[{i}].handler.kind '{kind}' is not a registered handler. Valid kinds: {string.Join(", ", handlers.Kinds)}.");
      return;
    }

    var result = handler.ValidateConfig(handlerNode["config"]?.ToJsonString());
    if (!result.IsValid)
    {
      errors.AddRange(result.Errors.Select(e => $"defaultTriggers[{i}].handler.config: {e}"));
    }
  }
}
```

- [ ] `src/toimi.tools.tietue/Types/TypeRepository.cs`: ctor `public class TypeRepository(TietueDbContext db, HandlerRegistry? handlers = null)` (add `using toimi.tools.tietue.Handlers;` + `using toimi.tools.tietue.Provisioning;`); replace the defaultTriggers parse-only block (:34-44) with:

```csharp
    if (defaultTriggersJson is not null)
    {
      var errors = TriggerTemplates.Validate(defaultTriggersJson, handlers);
      if (errors.Count > 0)
      {
        throw new TietueValidationException(errors);
      }
    }
```

- [ ] Verify the two pre-existing config-less templates in tests (`BehaviorPipelineTests`/`EntityRepositoryPostgresTests`: `[{"when":{"atField":"dueAt"},"handler":{"kind":"notify"}}]`) still pass — they use bare `new TypeRepository(db)` (null registry → structure-only). Full tietue suite green, including the Docker-gated tests if Docker is available.
- [ ] `dotnet format` both csproj + `--verify-no-changes`; commit `feat(tietue): define_type validates DefaultTriggers templates against registered handlers`.

---

## Task 6: Full gate + CLAUDE.md wording

**Files**
- Modify: `CLAUDE.md` (two bullets)

**Steps**

- [ ] Full verification, in order (`export PATH="/Users/jari/.local/share/mise/dotnet-root:$PATH"` first):
  - `dotnet build toimi.sln` — clean.
  - `dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj` — all green, count ≥ 325 (expected ≈ 360; Docker-gated tests run if Docker is up).
  - `dotnet test src/toimi.core.Tests/toimi.core.Tests.csproj` — 93 green (name per repo layout; locate with `ls src/*core*Tests*` if it differs).
  - `dotnet test src/toimi.web.Tests/toimi.web.Tests.csproj` — 38 green (same caveat).
  - `dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes` and the Tests csproj — both exit 0.
  - `grep -rn "Schedules\." src --include="*.cs" | grep -v obj` — zero hits (the old class is fully gone).
- [ ] CLAUDE.md touch-ups (wording only, keep both edits to one line each):
  - In the tietue **Model** bullet, after the `Trigger` shape: note the grammar owner — change `Trigger { EntityId, Schedule (one-shot `{at}` or recurring `{start,rrule,tz}`), HandlerKind+Config, NextFireAt }` to `Trigger { EntityId, Schedule (one-shot `{at}` or recurring `{start,rrule,tz}` — grammar owned by the `Schedule` value type; writes validate and reject invalid/exhausted schedules), HandlerKind+Config, NextFireAt }`.
  - In **Key Patterns → Triggers + scheduler**, append a sentence: `Trigger-writing paths validate at write time: TriggerRepository throws on invalid/exhausted schedules, handlers vet their configs via ValidateConfig (set_trigger/update_trigger/define_type); the scheduler and run_trigger fire whatever exists.`
- [ ] Self-review against the findings: (1) one parse — `Schedule.Parse` is the only schedule JSON reader (grep `JsonDocument.Parse`/`JsonNode.Parse` in `Scheduling/`, `Provisioning/`, `Tools/` for stragglers); (2) one resolve rule — `WithDefaultTz` called only inside `TriggerRepository.ResolveOrThrow` and the re-enable path parses stored (already-stamped) JSON; (3) the update_trigger regression test exists and its old silent-disable behavior is documented in the test comment; (4) handler config vocabulary is now declared beside each `HandleAsync`, enforced at all three write paths, with seeded types proven valid.
- [ ] Commit `docs: CLAUDE.md notes Schedule value type and write-time trigger validation` (or fold into a final `chore(tietue)` commit if other gate fixes were needed).
