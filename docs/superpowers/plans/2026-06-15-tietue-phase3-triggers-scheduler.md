# Tietue Phase 3 — Triggers, Scheduler & Native Handlers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the `tietue` engine reactive scheduling: per-entity **triggers** fired by a background **scheduler** that invokes deterministic **native handlers** (`notify`, `set-field`), recording every firing in a unified **`entity_events`** log. Types can declare **default triggers** that are stamped onto each instance at creation (copy-down). Seed a `reminder` standard type so reminders work through `tietue` — functionally replacing `muistutin` (the muistutin pod is deleted only at the Phase 6 cutover).

**Architecture:** A `triggers` table (instance-scoped, FK to `entities`) holds a JSON schedule spec (`{at}` one-shot or `{start,rrule,tz}` recurring), a handler kind + config, and a precomputed `NextFireAt`. A 1-minute `BackgroundService` (mirroring muistutin's `ReminderNotifier`) scans due triggers, dispatches the handler via a registry, records an `entity_events` row (idempotent via a unique key), and recomputes `NextFireAt` (RFC 5545 via `Ical.Net`) or disables one-shots. Completed occurrences (recorded as `complete` events) suppress further firing. Handlers are deterministic and testable; Qdrant/ntfy I/O is behind interfaces with fakes. `EntityRepository.CreateAsync` gains an optional `TriggerProvisioner` that resolves a type's default-trigger templates against the new entity's `Data` (copy-down).

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, `Ical.Net` 4.3.1 (RFC 5545), the existing `toimi.notifications` `NtfyClient`, `ModelContextProtocol` 1.1.0, xUnit + EF InMemory. Run dotnet inside the cached .NET 10 SDK Docker image (dotnet not on PATH): `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`. The repo enforces `dotnet format` (IDE0005 unused usings, IDE0022 block bodies, whitespace) as errors — run `dotnet format <csproj>` before each commit and verify with a real exit code (do not pipe `--verify-no-changes` to `tail`).

**Scope boundary (Phase 3 of the §16 build order):**
- IN: `triggers` + `entity_events` tables; RFC 5545 next-occurrence computation; the schedule spec + `NextFireAt`; `TriggerRepository`; the `notify` and `set-field` native handlers + a handler registry; the scheduler `BackgroundService`; completion semantics (`complete_occurrence`); copy-down default triggers + `define_type` `defaultTriggers` arg; `set_trigger`/`update_trigger`/`delete_trigger`/`list_triggers` MCP tools; `reminder` standard-type seeding; ntfy wiring + deployment env.
- OUT / DEFERRED (noted inline): **`poll-diff` handler** (needs HTTP fetch + extraction; it is the "Watch" capability for custom types, not required to retire muistutin — deferred to a later increment); the **`activate` MCP verb** (immediate off-cycle activation pairs with the Phase 4 message handler/inbox); the **`message` handler** (Phase 4); the **script sandbox** (Phase 5); deleting the muistutin pod/DB (Phase 6 cutover). Timezone handling mirrors muistutin (recurrence expansion is UTC-start-based; `tz` is stored, not used to shift DST — same simplification muistutin ships). Recurrence "next occurrence" uses a forward window (sparser-than-2-year rules won't schedule their next fire — `log` not relevant here, but noted as a known limit).

**Assumes Phases 1–2 are merged** (entities, type definitions with `Behaviors`, validation, repositories, MCP CRUD + search, semantic indexing, seeding, catalog injection).

---

## File Structure

**New in `src/toimi.tools.tietue/`:**
- `Data/Trigger.cs`, `Data/TriggerConfiguration.cs` — the trigger model + EF mapping
- `Data/EntityEvent.cs`, `Data/EntityEventConfiguration.cs` — the unified event log
- `Scheduling/RecurrenceCalculator.cs` — RFC 5545 next-occurrence (Ical.Net)
- `Scheduling/Schedules.cs` — schedule-spec parsing + `NextFireAt` computation
- `Scheduling/TriggerRepository.cs` — trigger CRUD
- `Scheduling/SchedulerTick.cs` — the testable "fire due triggers" unit
- `Scheduling/TriggerWorker.cs` — the `BackgroundService` wrapper
- `Events/EntityEventStore.cs` — record/idempotency/completion helpers
- `Handlers/INativeHandler.cs`, `HandlerContext.cs`, `HandlerResult.cs`, `HandlerRegistry.cs`
- `Handlers/NotifyHandler.cs`, `Handlers/SetFieldHandler.cs`
- `Handlers/TemplateRenderer.cs` — `{field}` substitution from `Data`
- `Notifications/INotifier.cs`, `NtfyNotifier.cs` — wraps the `toimi.notifications` `NtfyClient`
- `Provisioning/TriggerProvisioner.cs` — copy-down: resolve a type's default-trigger templates against an entity's `Data`
- `Tools/SetTriggerTool.cs`, `UpdateTriggerTool.cs`, `DeleteTriggerTool.cs`, `ListTriggersTool.cs`, `CompleteOccurrenceTool.cs`
- `Seed/` — `reminder` added to `TypeSeeder`

**Modified:**
- `toimi.tools.tietue.csproj` — add `Ical.Net`; add project ref to `toimi.notifications`
- `Data/TypeDefinition.cs` + `Data/TypeDefinitionConfiguration.cs` — add `DefaultTriggers` jsonb column
- `Data/TietueDbContext.cs` — `DbSet<Trigger>`, `DbSet<EntityEvent>`
- `Types/TypeRepository.cs` + `Tools/DefineTypeTool.cs` — `defaultTriggers` arg
- `Entities/EntityRepository.cs` — optional `TriggerProvisioner` (copy-down on create)
- `Seed/TypeSeeder.cs` — seed `reminder`
- `Program.cs` — register notifier, handlers, registry, trigger repo, provisioner, event store, scheduler hosted service; ntfy config
- `appsettings.json` — `Ntfy` section
- `k8s/base/tools-tietue/deployment.yaml` — ntfy secret env
- `Migrations/` — new migration `AddTriggersAndEvents`

**Test files** (`src/toimi.tools.tietue.Tests/`): one per logic task, plus a `FakeNotifier.cs`.

---

## Task 1: Packages + Trigger / EntityEvent / DefaultTriggers models

**Files:**
- Modify: `src/toimi.tools.tietue/toimi.tools.tietue.csproj`
- Create: `src/toimi.tools.tietue/Data/Trigger.cs`, `Data/TriggerConfiguration.cs`
- Create: `src/toimi.tools.tietue/Data/EntityEvent.cs`, `Data/EntityEventConfiguration.cs`
- Modify: `src/toimi.tools.tietue/Data/TypeDefinition.cs`, `Data/TypeDefinitionConfiguration.cs`, `Data/TietueDbContext.cs`
- Test: `src/toimi.tools.tietue.Tests/TriggerModelTests.cs`

- [ ] **Step 1: Add packages.** In `toimi.tools.tietue.csproj`, add to the package `<ItemGroup>`:
```xml
    <PackageReference Include="Ical.Net" Version="4.3.1" />
```
and add to the project-reference `<ItemGroup>` (next to the existing `toimi.core` ref):
```xml
    <ProjectReference Include="../toimi.notifications/toimi.notifications.csproj" />
```

- [ ] **Step 2: `Trigger` model.** `src/toimi.tools.tietue/Data/Trigger.cs`:
```csharp
namespace toimi.tools.tietue.Data;

public class Trigger
{
  public Guid Id { get; set; }
  public Guid EntityId { get; set; }

  // JSON schedule spec: {"at":"<iso utc>"} one-shot, or {"start":"<iso utc>","rrule":"FREQ=...","tz":"Europe/Helsinki"}.
  public required string Schedule { get; set; }

  public required string HandlerKind { get; set; }     // "notify" | "set-field"
  public string? HandlerConfig { get; set; }           // jsonb config for the handler

  public bool Enabled { get; set; } = true;
  public DateTimeOffset? NextFireAt { get; set; }
  public DateTimeOffset? LastFiredAt { get; set; }
  public DateTimeOffset CreatedAt { get; set; }
  public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 3: `TriggerConfiguration`.** `src/toimi.tools.tietue/Data/TriggerConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class TriggerConfiguration : IEntityTypeConfiguration<Trigger>
{
  public void Configure(EntityTypeBuilder<Trigger> builder)
  {
    builder.ToTable("triggers");
    builder.HasKey(t => t.Id);
    builder.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(t => t.Schedule).HasColumnType("jsonb").IsRequired();
    builder.Property(t => t.HandlerKind).IsRequired();
    builder.Property(t => t.HandlerConfig).HasColumnType("jsonb");
    builder.Property(t => t.Enabled).HasDefaultValue(true);
    builder.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
    builder.Property(t => t.UpdatedAt).HasDefaultValueSql("now()");

    builder.HasIndex(t => new { t.Enabled, t.NextFireAt });

    builder.HasOne<Entity>()
      .WithMany()
      .HasForeignKey(t => t.EntityId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
```

- [ ] **Step 4: `EntityEvent` model.** `src/toimi.tools.tietue/Data/EntityEvent.cs`:
```csharp
namespace toimi.tools.tietue.Data;

public class EntityEvent
{
  public Guid Id { get; set; }
  public Guid EntityId { get; set; }
  public DateTimeOffset OccurrenceUtc { get; set; }
  public required string Kind { get; set; }     // "notify" | "set-field" | "complete" | "observation"
  public required string Status { get; set; }   // "sent" | "done" | "applied" | ...
  public string? Result { get; set; }           // jsonb
  public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 5: `EntityEventConfiguration`.** `src/toimi.tools.tietue/Data/EntityEventConfiguration.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class EntityEventConfiguration : IEntityTypeConfiguration<EntityEvent>
{
  public void Configure(EntityTypeBuilder<EntityEvent> builder)
  {
    builder.ToTable("entity_events");
    builder.HasKey(e => e.Id);
    builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(e => e.Kind).IsRequired();
    builder.Property(e => e.Status).IsRequired();
    builder.Property(e => e.Result).HasColumnType("jsonb");
    builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

    builder.HasIndex(e => new { e.EntityId, e.OccurrenceUtc, e.Kind }).IsUnique();

    builder.HasOne<Entity>()
      .WithMany()
      .HasForeignKey(e => e.EntityId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
```

- [ ] **Step 6: `DefaultTriggers` column.** In `src/toimi.tools.tietue/Data/TypeDefinition.cs`, add after `Behaviors`:
```csharp
  public string? DefaultTriggers { get; set; }
```
In `src/toimi.tools.tietue/Data/TypeDefinitionConfiguration.cs`, inside `Configure`, add:
```csharp
    builder.Property(t => t.DefaultTriggers)
      .HasColumnType("jsonb");
```

- [ ] **Step 7: DbSets.** In `src/toimi.tools.tietue/Data/TietueDbContext.cs`, add:
```csharp
  public DbSet<Trigger> Triggers => Set<Trigger>();
  public DbSet<EntityEvent> EntityEvents => Set<EntityEvent>();
```

- [ ] **Step 8: Round-trip test.** `src/toimi.tools.tietue.Tests/TriggerModelTests.cs`:
```csharp
using toimi.tools.tietue.Data;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerModelTests
{
  [Fact]
  public async Task Trigger_and_event_round_trip()
  {
    using var db = TestDb.New();
    var entityId = Guid.NewGuid();
    var now = DateTimeOffset.UtcNow;

    db.Triggers.Add(new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = """{"at":"2026-06-20T09:00:00Z"}""",
      HandlerKind = "notify",
      NextFireAt = now,
      CreatedAt = now,
      UpdatedAt = now,
    });
    db.EntityEvents.Add(new EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = now,
      Kind = "notify",
      Status = "sent",
      CreatedAt = now,
    });
    await db.SaveChangesAsync();

    Assert.Single(db.Triggers);
    Assert.Single(db.EntityEvents);
  }
}
```

- [ ] **Step 9: Run the round-trip test + full suite** (existing 46 unaffected):
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
Expected: 47 pass.

- [ ] **Step 10: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Data src/toimi.tools.tietue/toimi.tools.tietue.csproj src/toimi.tools.tietue.Tests/TriggerModelTests.cs
git commit -m "feat(tietue): add trigger + entity_event models and default-triggers column"
```

---

## Task 2: Migration `AddTriggersAndEvents`

**Files:** `src/toimi.tools.tietue/Migrations/*` (generated)

- [ ] **Step 1: Generate the migration.**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet tool install --global dotnet-ef >/dev/null 2>&1 || true;
  export PATH="$PATH:/root/.dotnet/tools";
  dotnet ef migrations add AddTriggersAndEvents --project src/toimi.tools.tietue --startup-project src/toimi.tools.tietue
'
```

- [ ] **Step 2: Verify** it creates `triggers` + `entity_events` with the jsonb columns, the unique `(entity_id, occurrence_utc, kind)` index, the FKs, and the `default_triggers` column on `type_definitions`:
`grep -n "triggers\|entity_events\|default_triggers\|jsonb\|IsUnique\|unique" src/toimi.tools.tietue/Migrations/*_AddTriggersAndEvents.cs`

- [ ] **Step 3: Build.** `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj` → `Build succeeded.`

- [ ] **Step 4: Commit.**
```bash
git add src/toimi.tools.tietue/Migrations
git commit -m "feat(tietue): migration for triggers, entity_events, default_triggers"
```

---

## Task 3: `RecurrenceCalculator` — RFC 5545 next-occurrence

**Files:**
- Create: `src/toimi.tools.tietue/Scheduling/RecurrenceCalculator.cs`
- Test: `src/toimi.tools.tietue.Tests/RecurrenceCalculatorTests.cs`

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/RecurrenceCalculatorTests.cs`:
```csharp
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class RecurrenceCalculatorTests
{
  private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero); // Mon 2026-06-01 09:00Z

  [Fact]
  public void Daily_next_after_returns_next_day()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void OnOrAfter_is_inclusive_of_an_exact_occurrence()
  {
    var next = RecurrenceCalculator.NextOccurrenceOnOrAfter(Start, "FREQ=DAILY", new(2026, 6, 3, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 3, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Bounded_rule_returns_null_after_last_occurrence()
  {
    // COUNT=3 daily: 06-01, 06-02, 06-03. After 06-03 there is none.
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=DAILY;COUNT=3", new(2026, 6, 3, 9, 0, 0, TimeSpan.Zero));
    Assert.Null(next);
  }

  [Fact]
  public void Weekly_byday_skips_to_matching_weekday()
  {
    var next = RecurrenceCalculator.NextOccurrenceAfter(Start, "FREQ=WEEKLY;BYDAY=MO", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 8, 9, 0, 0, TimeSpan.Zero), next);
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement** (mirrors muistutin's `RecurrenceExpander` use of Ical.Net 4.3.1).

`src/toimi.tools.tietue/Scheduling/RecurrenceCalculator.cs`:
```csharp
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;

namespace toimi.tools.tietue.Scheduling;

public static class RecurrenceCalculator
{
  // Forward search window; rules sparser than this won't schedule their next fire (documented limitation).
  private static readonly TimeSpan Window = TimeSpan.FromDays(366 * 2);

  public static DateTimeOffset? NextOccurrenceAfter(DateTimeOffset start, string rrule, DateTimeOffset after) =>
    FirstOccurrence(start, rrule, after, inclusive: false);

  public static DateTimeOffset? NextOccurrenceOnOrAfter(DateTimeOffset start, string rrule, DateTimeOffset after) =>
    FirstOccurrence(start, rrule, after, inclusive: true);

  private static DateTimeOffset? FirstOccurrence(DateTimeOffset start, string rrule, DateTimeOffset after, bool inclusive)
  {
    var calendar = new Calendar();
    calendar.Events.Add(new CalendarEvent
    {
      Start = new CalDateTime(start.UtcDateTime),
      End = new CalDateTime(start.AddHours(1).UtcDateTime),
      RecurrenceRules = [new RecurrencePattern(rrule)],
    });

    // Search from just before `after` so an exact occurrence at `after` is included when inclusive.
    var from = after.AddSeconds(-1).UtcDateTime;
    var to = after.Add(Window).UtcDateTime;

    return calendar.GetOccurrences(new CalDateTime(from), new CalDateTime(to))
      .Select(o => o.Period.StartTime.AsDateTimeOffset)
      .Where(o => inclusive ? o >= after : o > after)
      .OrderBy(o => o)
      .Cast<DateTimeOffset?>()
      .FirstOrDefault();
  }
}
```

- [ ] **Step 4: Run, confirm 4 PASS.** (If the Ical.Net occurrence API differs, align exactly with `src/toimi.tools.muistutin/Recurrence/RecurrenceExpander.cs`, which uses the same 4.3.1 API.)

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Scheduling/RecurrenceCalculator.cs src/toimi.tools.tietue.Tests/RecurrenceCalculatorTests.cs
git commit -m "feat(tietue): add RFC 5545 next-occurrence calculator"
```

---

## Task 4: `Schedules` — schedule-spec parsing + NextFireAt

**Files:**
- Create: `src/toimi.tools.tietue/Scheduling/Schedules.cs`
- Test: `src/toimi.tools.tietue.Tests/SchedulesTests.cs`

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/SchedulesTests.cs`:
```csharp
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchedulesTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  [Fact]
  public void OneShot_initial_is_the_at_time()
  {
    var next = Schedules.InitialNextFireAt("""{"at":"2026-06-01T09:00:00Z"}""", Now);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void OneShot_next_after_fire_is_null()
  {
    Assert.Null(Schedules.NextAfter("""{"at":"2026-06-01T09:00:00Z"}""", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero)));
  }

  [Fact]
  public void Recurring_initial_is_first_occurrence_on_or_after_now()
  {
    var next = Schedules.InitialNextFireAt("""{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", Now);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Recurring_next_after_is_following_occurrence()
  {
    var next = Schedules.NextAfter("""{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero));
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), next);
  }

  [Fact]
  public void Malformed_schedule_yields_null()
  {
    Assert.Null(Schedules.InitialNextFireAt("{ not json", Now));
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement.** `src/toimi.tools.tietue/Scheduling/Schedules.cs`:
```csharp
using System.Text.Json;

namespace toimi.tools.tietue.Scheduling;

public static class Schedules
{
  // First fire time for a freshly-created trigger.
  public static DateTimeOffset? InitialNextFireAt(string scheduleJson, DateTimeOffset now)
  {
    var spec = Parse(scheduleJson);
    if (spec is null)
    {
      return null;
    }

    if (spec.At is { } at)
    {
      return at; // fire even if slightly in the past (fire-late then done)
    }

    if (spec.Start is { } start && spec.Rrule is { } rrule)
    {
      var anchor = start > now ? start : now;
      return RecurrenceCalculator.NextOccurrenceOnOrAfter(start, rrule, anchor);
    }

    return null;
  }

  // Next fire time strictly after a just-fired occurrence (null = done/disable).
  public static DateTimeOffset? NextAfter(string scheduleJson, DateTimeOffset firedOccurrence)
  {
    var spec = Parse(scheduleJson);
    if (spec is null || spec.At is not null)
    {
      return null; // one-shot is done
    }

    if (spec.Start is { } start && spec.Rrule is { } rrule)
    {
      return RecurrenceCalculator.NextOccurrenceAfter(start, rrule, firedOccurrence);
    }

    return null;
  }

  private sealed record Spec(DateTimeOffset? At, DateTimeOffset? Start, string? Rrule, string? Tz);

  private static Spec? Parse(string scheduleJson)
  {
    try
    {
      using var doc = JsonDocument.Parse(scheduleJson);
      var root = doc.RootElement;
      DateTimeOffset? at = root.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String
        ? DateTimeOffset.Parse(a.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        : null;
      DateTimeOffset? start = root.TryGetProperty("start", out var s) && s.ValueKind == JsonValueKind.String
        ? DateTimeOffset.Parse(s.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)
        : null;
      var rrule = root.TryGetProperty("rrule", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
      var tz = root.TryGetProperty("tz", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
      return new Spec(at, start, rrule, tz);
    }
    catch (Exception ex) when (ex is JsonException or FormatException)
    {
      return null;
    }
  }
}
```

- [ ] **Step 4: Run, confirm 5 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Scheduling/Schedules.cs src/toimi.tools.tietue.Tests/SchedulesTests.cs
git commit -m "feat(tietue): add schedule spec parsing and NextFireAt computation"
```

---

## Task 5: `TriggerRepository` — trigger CRUD

**Files:**
- Create: `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs`
- Test: `src/toimi.tools.tietue.Tests/TriggerRepositoryTests.cs`

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/TriggerRepositoryTests.cs`:
```csharp
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerRepositoryTests
{
  private static readonly DateTimeOffset Now = new(2026, 6, 1, 8, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task Create_computes_next_fire_at_from_schedule()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var entityId = Guid.NewGuid();

    var t = await repo.CreateAsync(entityId, """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.NotEqual(Guid.Empty, t.Id);
    Assert.Equal(new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), t.NextFireAt);
    Assert.True(t.Enabled);
  }

  [Fact]
  public async Task List_by_entity_returns_its_triggers()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var e1 = Guid.NewGuid();
    await repo.CreateAsync(e1, """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);
    await repo.CreateAsync(Guid.NewGuid(), """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.Single(await repo.ListByEntityAsync(e1));
  }

  [Fact]
  public async Task Delete_removes_trigger()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(Guid.NewGuid(), """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    Assert.True(await repo.DeleteAsync(t.Id));
    Assert.Null(await repo.GetAsync(t.Id));
  }

  [Fact]
  public async Task Update_replaces_schedule_and_recomputes_next_fire()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(Guid.NewGuid(), """{"at":"2026-06-01T09:00:00Z"}""", "notify", null, Now);

    var updated = await repo.UpdateAsync(t.Id, """{"at":"2026-06-02T09:00:00Z"}""", null, null, Now);

    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), updated!.NextFireAt);
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement.** `src/toimi.tools.tietue/Scheduling/TriggerRepository.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Scheduling;

public class TriggerRepository(TietueDbContext db)
{
  public async Task<Trigger> CreateAsync(Guid entityId, string scheduleJson, string handlerKind, string? handlerConfig, DateTimeOffset now, CancellationToken ct = default)
  {
    var trigger = new Trigger
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      Schedule = scheduleJson,
      HandlerKind = handlerKind,
      HandlerConfig = handlerConfig,
      Enabled = true,
      NextFireAt = Schedules.InitialNextFireAt(scheduleJson, now),
      CreatedAt = now,
      UpdatedAt = now,
    };
    db.Triggers.Add(trigger);
    await db.SaveChangesAsync(ct);
    return trigger;
  }

  public Task<Trigger?> GetAsync(Guid id, CancellationToken ct = default) =>
    db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);

  public async Task<IReadOnlyList<Trigger>> ListByEntityAsync(Guid entityId, CancellationToken ct = default) =>
    await db.Triggers.Where(t => t.EntityId == entityId).OrderBy(t => t.CreatedAt).ToListAsync(ct);

  public async Task<Trigger?> UpdateAsync(Guid id, string? scheduleJson, string? handlerConfig, bool? enabled, DateTimeOffset now, CancellationToken ct = default)
  {
    var trigger = await db.Triggers.FirstOrDefaultAsync(t => t.Id == id, ct);
    if (trigger is null)
    {
      return null;
    }

    if (scheduleJson is not null)
    {
      trigger.Schedule = scheduleJson;
      trigger.NextFireAt = Schedules.InitialNextFireAt(scheduleJson, now);
    }

    if (handlerConfig is not null)
    {
      trigger.HandlerConfig = handlerConfig;
    }

    if (enabled is not null)
    {
      trigger.Enabled = enabled.Value;
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
}
```

- [ ] **Step 4: Run, confirm 4 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Scheduling/TriggerRepository.cs src/toimi.tools.tietue.Tests/TriggerRepositoryTests.cs
git commit -m "feat(tietue): add trigger repository"
```

---

## Task 6: `EntityEventStore` — recording, idempotency, completion

**Files:**
- Create: `src/toimi.tools.tietue/Events/EntityEventStore.cs`
- Test: `src/toimi.tools.tietue.Tests/EntityEventStoreTests.cs`

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/EntityEventStoreTests.cs`:
```csharp
using toimi.tools.tietue.Events;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class EntityEventStoreTests
{
  private static readonly DateTimeOffset Occ = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

  [Fact]
  public async Task Records_an_event()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();

    await store.RecordAsync(e, Occ, "notify", "sent", null);

    Assert.True(await store.HasEventAsync(e, Occ, "notify"));
  }

  [Fact]
  public async Task Occurrence_handled_when_kind_or_complete_present()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();
    await store.RecordAsync(e, Occ, "complete", "done", null);

    // A completed occurrence is considered handled for a notify trigger (suppresses firing).
    Assert.True(await store.OccurrenceHandledAsync(e, Occ, "notify"));
  }

  [Fact]
  public async Task Unhandled_when_no_matching_event()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    Assert.False(await store.OccurrenceHandledAsync(Guid.NewGuid(), Occ, "notify"));
  }

  [Fact]
  public async Task Complete_is_idempotent()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var e = Guid.NewGuid();

    await store.CompleteAsync(e, Occ);
    await store.CompleteAsync(e, Occ);

    Assert.True(await store.OccurrenceHandledAsync(e, Occ, "notify"));
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement.** `src/toimi.tools.tietue/Events/EntityEventStore.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Events;

public class EntityEventStore(TietueDbContext db)
{
  public async Task RecordAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, string status, string? result, CancellationToken ct = default)
  {
    db.EntityEvents.Add(new EntityEvent
    {
      Id = Guid.NewGuid(),
      EntityId = entityId,
      OccurrenceUtc = occurrenceUtc,
      Kind = kind,
      Status = status,
      Result = result,
      CreatedAt = DateTimeOffset.UtcNow,
    });
    await db.SaveChangesAsync(ct);
  }

  public Task<bool> HasEventAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, CancellationToken ct = default) =>
    db.EntityEvents.AnyAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && e.Kind == kind, ct);

  // True if this occurrence was already handled by the same kind OR completed by the user.
  public Task<bool> OccurrenceHandledAsync(Guid entityId, DateTimeOffset occurrenceUtc, string kind, CancellationToken ct = default) =>
    db.EntityEvents.AnyAsync(e => e.EntityId == entityId && e.OccurrenceUtc == occurrenceUtc && (e.Kind == kind || e.Kind == "complete"), ct);

  public async Task CompleteAsync(Guid entityId, DateTimeOffset occurrenceUtc, CancellationToken ct = default)
  {
    if (!await HasEventAsync(entityId, occurrenceUtc, "complete", ct))
    {
      await RecordAsync(entityId, occurrenceUtc, "complete", "done", null, ct);
    }
  }
}
```

- [ ] **Step 4: Run, confirm 4 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Events src/toimi.tools.tietue.Tests/EntityEventStoreTests.cs
git commit -m "feat(tietue): add entity event store with completion semantics"
```

---

## Task 7: Native handlers — interface, `TemplateRenderer`, `NotifyHandler`

**Files:**
- Create: `src/toimi.tools.tietue/Handlers/HandlerResult.cs`, `HandlerContext.cs`, `INativeHandler.cs`, `TemplateRenderer.cs`, `NotifyHandler.cs`
- Create: `src/toimi.tools.tietue/Notifications/INotifier.cs`, `NtfyNotifier.cs`
- Create: `src/toimi.tools.tietue.Tests/FakeNotifier.cs`
- Test: `src/toimi.tools.tietue.Tests/TemplateRendererTests.cs`, `NotifyHandlerTests.cs`

- [ ] **Step 1: Define the handler contracts.**

`src/toimi.tools.tietue/Handlers/HandlerResult.cs`:
```csharp
namespace toimi.tools.tietue.Handlers;

public record HandlerResult(string Status, string? Result = null);
```

`src/toimi.tools.tietue/Handlers/HandlerContext.cs`:
```csharp
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Handlers;

public record HandlerContext(Entity Entity, string? ConfigJson, DateTimeOffset OccurrenceUtc);
```

`src/toimi.tools.tietue/Handlers/INativeHandler.cs`:
```csharp
namespace toimi.tools.tietue.Handlers;

public interface INativeHandler
{
  string Kind { get; }

  Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default);
}
```

- [ ] **Step 2: Notifier abstraction.**

`src/toimi.tools.tietue/Notifications/INotifier.cs`:
```csharp
namespace toimi.tools.tietue.Notifications;

public interface INotifier
{
  Task SendAsync(string message, string? title, string priority, string? tags, CancellationToken ct = default);
}
```

`src/toimi.tools.tietue/Notifications/NtfyNotifier.cs` (wraps the shared `toimi.notifications` client):
```csharp
using Toimi.Notifications;

namespace toimi.tools.tietue.Notifications;

public class NtfyNotifier(NtfyClient client) : INotifier
{
  public Task SendAsync(string message, string? title, string priority, string? tags, CancellationToken ct = default) =>
    client.SendAsync(message, title, priority, tags, ct);
}
```
> Confirm the `toimi.notifications` namespace + `NtfyClient.SendAsync` signature by opening `src/toimi.notifications/NtfyClient.cs`; adjust the `using`/call to match exactly (it is `SendAsync(string message, string? title = null, string priority = "default", string? tags = null, CancellationToken ct = default)`).

`src/toimi.tools.tietue.Tests/FakeNotifier.cs`:
```csharp
using toimi.tools.tietue.Notifications;

namespace toimi.tools.tietue.Tests;

public class FakeNotifier : INotifier
{
  public List<(string Message, string? Title, string Priority, string? Tags)> Sent { get; } = [];

  public Task SendAsync(string message, string? title, string priority, string? tags, CancellationToken ct = default)
  {
    Sent.Add((message, title, priority, tags));
    return Task.CompletedTask;
  }
}
```

- [ ] **Step 3: `TemplateRenderer` tests.** `src/toimi.tools.tietue.Tests/TemplateRendererTests.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TemplateRendererTests
{
  private static JsonDocument Doc(string j) => JsonDocument.Parse(j);

  [Fact]
  public void Substitutes_braced_fields()
  {
    var s = TemplateRenderer.Render("Hi {name}, due {when}", Doc("""{"name":"Jari","when":"9am"}"""));
    Assert.Equal("Hi Jari, due 9am", s);
  }

  [Fact]
  public void Missing_field_becomes_empty()
  {
    Assert.Equal("Hi ", TemplateRenderer.Render("Hi {name}", Doc("""{}""")));
  }

  [Fact]
  public void Null_template_returns_empty()
  {
    Assert.Equal("", TemplateRenderer.Render(null, Doc("""{"a":1}""")));
  }
}
```

- [ ] **Step 4: Implement `TemplateRenderer`.** `src/toimi.tools.tietue/Handlers/TemplateRenderer.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace toimi.tools.tietue.Handlers;

public static partial class TemplateRenderer
{
  [GeneratedRegex(@"\{(\w+)\}")]
  private static partial Regex TokenRegex();

  public static string Render(string? template, JsonDocument data)
  {
    if (string.IsNullOrEmpty(template))
    {
      return "";
    }

    return TokenRegex().Replace(template, m =>
    {
      var key = m.Groups[1].Value;
      if (!data.RootElement.TryGetProperty(key, out var v))
      {
        return "";
      }

      return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
    });
  }
}
```

- [ ] **Step 5: `NotifyHandler` tests.** `src/toimi.tools.tietue.Tests/NotifyHandlerTests.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class NotifyHandlerTests
{
  private static Entity Reminder(string title, string desc) => new()
  {
    Id = Guid.NewGuid(),
    Type = "reminder",
    Data = JsonDocument.Parse($$"""{"title":"{{title}}","description":"{{desc}}"}"""),
    CreatedAt = DateTimeOffset.UtcNow,
    UpdatedAt = DateTimeOffset.UtcNow,
  };

  [Fact]
  public async Task Sends_rendered_notification()
  {
    var notifier = new FakeNotifier();
    var handler = new NotifyHandler(notifier);
    var config = """{"titleTemplate":"{title}","messageTemplate":"{description}","priority":"high","tags":"bell"}""";

    var result = await handler.HandleAsync(new HandlerContext(Reminder("Call mom", "use the new number"), config, DateTimeOffset.UtcNow));

    var sent = Assert.Single(notifier.Sent);
    Assert.Equal("Call mom", sent.Title);
    Assert.Equal("use the new number", sent.Message);
    Assert.Equal("high", sent.Priority);
    Assert.Equal("sent", result.Status);
  }

  [Fact]
  public async Task Falls_back_to_title_when_message_template_absent()
  {
    var notifier = new FakeNotifier();
    var handler = new NotifyHandler(notifier);

    await handler.HandleAsync(new HandlerContext(Reminder("Standup", ""), """{"titleTemplate":"{title}"}""", DateTimeOffset.UtcNow));

    Assert.Equal("Standup", notifier.Sent.Single().Message);
  }
}
```

- [ ] **Step 6: Implement `NotifyHandler`.** `src/toimi.tools.tietue/Handlers/NotifyHandler.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Notifications;

namespace toimi.tools.tietue.Handlers;

public class NotifyHandler(INotifier notifier) : INativeHandler
{
  public string Kind => "notify";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    string? titleTemplate = null, messageTemplate = null, priority = "default", tags = null;
    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      var root = cfg.RootElement;
      titleTemplate = Str(root, "titleTemplate");
      messageTemplate = Str(root, "messageTemplate");
      priority = Str(root, "priority") ?? "default";
      tags = Str(root, "tags");
    }

    var title = TemplateRenderer.Render(titleTemplate, ctx.Entity.Data);
    var message = TemplateRenderer.Render(messageTemplate, ctx.Entity.Data);
    if (string.IsNullOrEmpty(message))
    {
      message = title; // fall back to the title
    }

    await notifier.SendAsync(message, string.IsNullOrEmpty(title) ? null : title, priority, tags, ct);
    return new HandlerResult("sent");
  }

  private static string? Str(JsonElement e, string name) =>
    e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 7: Run the new tests (TemplateRenderer 3 + NotifyHandler 2), confirm PASS.**

- [ ] **Step 8: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Handlers src/toimi.tools.tietue/Notifications src/toimi.tools.tietue.Tests/FakeNotifier.cs src/toimi.tools.tietue.Tests/TemplateRendererTests.cs src/toimi.tools.tietue.Tests/NotifyHandlerTests.cs
git commit -m "feat(tietue): add native handler contracts, template renderer, notify handler"
```

---

## Task 8: `SetFieldHandler` + `HandlerRegistry`

**Files:**
- Create: `src/toimi.tools.tietue/Handlers/SetFieldHandler.cs`, `HandlerRegistry.cs`
- Test: `src/toimi.tools.tietue.Tests/SetFieldHandlerTests.cs`, `HandlerRegistryTests.cs`

- [ ] **Step 1: `SetFieldHandler` tests.** `src/toimi.tools.tietue.Tests/SetFieldHandlerTests.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SetFieldHandlerTests
{
  private const string Schema = """{"type":"object","properties":{"status":{"type":"string"}}}""";

  [Fact]
  public async Task Sets_a_data_field_via_repository()
  {
    using var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("task", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var e = await repo.CreateAsync("task", JsonNode.Parse("""{"status":"open"}"""), []);

    var handler = new SetFieldHandler(repo);
    var result = await handler.HandleAsync(new HandlerContext(e, """{"path":"status","value":"done"}""", DateTimeOffset.UtcNow));

    var reloaded = await repo.GetAsync(e.Id);
    Assert.Equal("done", reloaded!.Data.RootElement.GetProperty("status").GetString());
    Assert.Equal("applied", result.Status);
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement `SetFieldHandler`.** It mutates one `Data` field and persists via `EntityRepository.UpdateAsync` (which re-validates + re-indexes). `src/toimi.tools.tietue/Handlers/SetFieldHandler.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Handlers;

public class SetFieldHandler(EntityRepository repository) : INativeHandler
{
  public string Kind => "set-field";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    using var cfg = JsonDocument.Parse(ctx.ConfigJson ?? "{}");
    var path = cfg.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    if (string.IsNullOrEmpty(path))
    {
      return new HandlerResult("skipped", """{"reason":"no path"}""");
    }

    var value = cfg.RootElement.TryGetProperty("value", out var v) ? JsonNode.Parse(v.GetRawText()) : null;

    var data = JsonNode.Parse(ctx.Entity.Data.RootElement.GetRawText())!.AsObject();
    data[path] = value;
    await repository.UpdateAsync(ctx.Entity.Id, data, null, ct);

    return new HandlerResult("applied", $$"""{"path":"{{path}}"}""");
  }
}
```

- [ ] **Step 4: Run, confirm PASS.**

- [ ] **Step 5: `HandlerRegistry` tests.** `src/toimi.tools.tietue.Tests/HandlerRegistryTests.cs`:
```csharp
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class HandlerRegistryTests
{
  [Fact]
  public void Resolves_by_kind_and_returns_null_for_unknown()
  {
    var notify = new NotifyHandler(new FakeNotifier());
    var registry = new HandlerRegistry([notify]);

    Assert.Same(notify, registry.Resolve("notify"));
    Assert.Null(registry.Resolve("nope"));
  }
}
```

- [ ] **Step 6: Implement `HandlerRegistry`.** `src/toimi.tools.tietue/Handlers/HandlerRegistry.cs`:
```csharp
namespace toimi.tools.tietue.Handlers;

public class HandlerRegistry
{
  private readonly Dictionary<string, INativeHandler> _byKind;

  public HandlerRegistry(IEnumerable<INativeHandler> handlers) =>
    _byKind = handlers.ToDictionary(h => h.Kind);

  public INativeHandler? Resolve(string kind) =>
    _byKind.GetValueOrDefault(kind);
}
```

- [ ] **Step 7: Run the registry test, confirm PASS.**

- [ ] **Step 8: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Handlers/SetFieldHandler.cs src/toimi.tools.tietue/Handlers/HandlerRegistry.cs src/toimi.tools.tietue.Tests/SetFieldHandlerTests.cs src/toimi.tools.tietue.Tests/HandlerRegistryTests.cs
git commit -m "feat(tietue): add set-field handler and handler registry"
```

---

## Task 9: `SchedulerTick` — fire due triggers

**Files:**
- Create: `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs`
- Test: `src/toimi.tools.tietue.Tests/SchedulerTickTests.cs`

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/SchedulerTickTests.cs`:
```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SchedulerTickTests
{
  private const string Schema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

  private static async Task<(toimi.tools.tietue.Data.TietueDbContext db, FakeNotifier notifier, SchedulerTick tick, EntityRepository repo)> SetupAsync()
  {
    var db = TestDb.New();
    await new TypeRepository(db).DefineAsync("reminder", Schema);
    var repo = new EntityRepository(db, new SchemaValidator());
    var notifier = new FakeNotifier();
    var registry = new HandlerRegistry([new NotifyHandler(notifier)]);
    var tick = new SchedulerTick(db, registry, new EntityEventStore(db));
    return (db, notifier, tick, repo);
  }

  [Fact]
  public async Task Fires_due_one_shot_then_disables_it()
  {
    var (db, notifier, tick, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Call"}"""), []);
    await new TriggerRepository(db).CreateAsync(e.Id, """{"at":"2026-06-01T09:00:00Z"}""", "notify",
      """{"titleTemplate":"{title}"}""", new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    Assert.Single(notifier.Sent);
    var trigger = (await new TriggerRepository(db).ListByEntityAsync(e.Id))[0];
    Assert.False(trigger.Enabled);
    Assert.Null(trigger.NextFireAt);
    Assert.True(await new EntityEventStore(db).HasEventAsync(e.Id, new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero), "notify"));
  }

  [Fact]
  public async Task Recurring_reschedules_next_fire()
  {
    var (db, notifier, tick, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Standup"}"""), []);
    await new TriggerRepository(db).CreateAsync(e.Id, """{"start":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""", "notify",
      """{"titleTemplate":"{title}"}""", new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    var trigger = (await new TriggerRepository(db).ListByEntityAsync(e.Id))[0];
    Assert.True(trigger.Enabled);
    Assert.Equal(new DateTimeOffset(2026, 6, 2, 9, 0, 0, TimeSpan.Zero), trigger.NextFireAt);
  }

  [Fact]
  public async Task Does_not_fire_a_completed_occurrence()
  {
    var (db, notifier, tick, repo) = await SetupAsync();
    using var _ = db;
    var e = await repo.CreateAsync("reminder", JsonNode.Parse("""{"title":"Skip me"}"""), []);
    var occ = new DateTimeOffset(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);
    await new TriggerRepository(db).CreateAsync(e.Id, """{"at":"2026-06-01T09:00:00Z"}""", "notify",
      """{"titleTemplate":"{title}"}""", new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.Zero));
    await new EntityEventStore(db).CompleteAsync(e.Id, occ);

    await tick.RunDueAsync(new DateTimeOffset(2026, 6, 1, 9, 1, 0, TimeSpan.Zero), default);

    Assert.Empty(notifier.Sent);
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement.** `src/toimi.tools.tietue/Scheduling/SchedulerTick.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Handlers;

namespace toimi.tools.tietue.Scheduling;

public class SchedulerTick(TietueDbContext db, HandlerRegistry handlers, EntityEventStore events)
{
  public async Task RunDueAsync(DateTimeOffset now, CancellationToken ct)
  {
    var due = await db.Triggers
      .Where(t => t.Enabled && t.NextFireAt != null && t.NextFireAt <= now)
      .OrderBy(t => t.NextFireAt)
      .ToListAsync(ct);

    foreach (var trigger in due)
    {
      if (ct.IsCancellationRequested)
      {
        break;
      }

      var occurrence = trigger.NextFireAt!.Value;
      var entity = await db.Entities.FirstOrDefaultAsync(e => e.Id == trigger.EntityId, ct);

      if (entity is not null && !await events.OccurrenceHandledAsync(trigger.EntityId, occurrence, trigger.HandlerKind, ct))
      {
        var handler = handlers.Resolve(trigger.HandlerKind);
        if (handler is not null)
        {
          var result = await handler.HandleAsync(new HandlerContext(entity, trigger.HandlerConfig, occurrence), ct);
          await events.RecordAsync(trigger.EntityId, occurrence, trigger.HandlerKind, result.Status, result.Result, ct);
        }
      }

      trigger.LastFiredAt = occurrence;
      trigger.NextFireAt = Schedules.NextAfter(trigger.Schedule, occurrence);
      if (trigger.NextFireAt is null)
      {
        trigger.Enabled = false;
      }

      trigger.UpdatedAt = now;
      await db.SaveChangesAsync(ct);
    }
  }
}
```
> Single-threaded sequential processing of due triggers gives the design's per-entity serial guarantee (one worker, one tick at a time). `OccurrenceHandledAsync` + the unique `(entity, occurrence, kind)` index make firing idempotent across restarts.

- [ ] **Step 4: Run, confirm 3 PASS.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Scheduling/SchedulerTick.cs src/toimi.tools.tietue.Tests/SchedulerTickTests.cs
git commit -m "feat(tietue): add scheduler tick that fires due triggers idempotently"
```

---

## Task 10: `TriggerWorker` background service

**Files:**
- Create: `src/toimi.tools.tietue/Scheduling/TriggerWorker.cs`

> Not unit-tested (a timer loop, like muistutin's `ReminderNotifier`); the logic lives in the tested `SchedulerTick`. Mirror muistutin's `ReminderNotifier` structure exactly.

- [ ] **Step 1: Implement.** `src/toimi.tools.tietue/Scheduling/TriggerWorker.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace toimi.tools.tietue.Scheduling;

public class TriggerWorker(IServiceScopeFactory scopeFactory, ILogger<TriggerWorker> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("Tietue trigger worker started.");
    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        using var scope = scopeFactory.CreateScope();
        var tick = scope.ServiceProvider.GetRequiredService<SchedulerTick>();
        await tick.RunDueAsync(DateTimeOffset.UtcNow, stoppingToken);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Error in tietue trigger worker loop.");
      }

      await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
    }
  }
}
```

- [ ] **Step 2: Build the main project.** `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet build src/toimi.tools.tietue/toimi.tools.tietue.csproj` → success.

- [ ] **Step 3: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj
git add src/toimi.tools.tietue/Scheduling/TriggerWorker.cs
git commit -m "feat(tietue): add trigger worker background service"
```

---

## Task 11: Copy-down — `TriggerProvisioner` + wire into create + `define_type`

**Files:**
- Create: `src/toimi.tools.tietue/Provisioning/TriggerProvisioner.cs`
- Modify: `src/toimi.tools.tietue/Entities/EntityRepository.cs`, `Types/TypeRepository.cs`, `Tools/DefineTypeTool.cs`
- Test: `src/toimi.tools.tietue.Tests/TriggerProvisionerTests.cs`

The default-trigger template shape (stored in `TypeDefinition.DefaultTriggers`, a JSON array):
```json
[{ "when": { "atField": "dueAt", "rruleField": "rrule", "tzField": "timezone" },
   "handler": { "kind": "notify", "config": { "titleTemplate": "{title}", "messageTemplate": "{description}" } } }]
```
At entity creation, the provisioner resolves `when` against the entity's `Data`: `atField` → the `at`/`start` time; if `rruleField` is present in `Data`, the trigger is recurring (`{start, rrule, tz}`), else one-shot (`{at}`). The handler is copied verbatim (its templates are rendered at fire time).

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/TriggerProvisionerTests.cs`:
```csharp
using System.Text.Json.Nodes;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Provisioning;
using toimi.tools.tietue.Scheduling;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerProvisionerTests
{
  private const string DefaultTriggers = """
  [{"when":{"atField":"dueAt","rruleField":"rrule","tzField":"timezone"},
    "handler":{"kind":"notify","config":{"titleTemplate":"{title}"}}}]
  """;

  private static Entity Reminder(string dataJson) => new()
  {
    Id = Guid.NewGuid(),
    Type = "reminder",
    Data = System.Text.Json.JsonDocument.Parse(dataJson),
    CreatedAt = DateTimeOffset.UtcNow,
    UpdatedAt = DateTimeOffset.UtcNow,
  };

  [Fact]
  public async Task Provisions_one_shot_trigger_from_due_field()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db));
    var e = Reminder("""{"title":"Call","dueAt":"2026-06-20T09:00:00Z"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var triggers = await new TriggerRepository(db).ListByEntityAsync(e.Id);
    var t = Assert.Single(triggers);
    Assert.Equal("notify", t.HandlerKind);
    Assert.Equal(new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero), t.NextFireAt);
  }

  [Fact]
  public async Task Provisions_recurring_trigger_when_rrule_present()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db));
    var e = Reminder("""{"title":"Standup","dueAt":"2026-06-01T09:00:00Z","rrule":"FREQ=DAILY"}""");

    await provisioner.ProvisionAsync(e, DefaultTriggers, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    var t = Assert.Single(await new TriggerRepository(db).ListByEntityAsync(e.Id));
    Assert.Contains("FREQ=DAILY", t.Schedule);
  }

  [Fact]
  public async Task No_triggers_when_definition_is_null()
  {
    using var db = TestDb.New();
    var provisioner = new TriggerProvisioner(new TriggerRepository(db));
    var e = Reminder("""{"title":"x"}""");

    await provisioner.ProvisionAsync(e, null, DateTimeOffset.UtcNow);

    Assert.Empty(await new TriggerRepository(db).ListByEntityAsync(e.Id));
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement `TriggerProvisioner`.** `src/toimi.tools.tietue/Provisioning/TriggerProvisioner.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Provisioning;

public class TriggerProvisioner(TriggerRepository triggers)
{
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
        continue;
      }

      var handler = template["handler"]?.AsObject();
      var kind = handler?["kind"]?.GetValue<string>();
      if (string.IsNullOrEmpty(kind))
      {
        continue;
      }

      var config = handler?["config"]?.ToJsonString();
      await triggers.CreateAsync(entity.Id, schedule, kind, config, now, ct);
    }
  }

  // Resolves a "when" template against the entity Data into a schedule-spec JSON, or null if the time field is missing.
  private static string? BuildSchedule(JsonObject? when, JsonDocument data)
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

    var at = atVal.GetString();
    var rruleField = when["rruleField"]?.GetValue<string>();
    var tzField = when["tzField"]?.GetValue<string>();

    var hasRrule = rruleField is not null && data.RootElement.TryGetProperty(rruleField, out var rr)
      && rr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(rr.GetString());

    if (hasRrule)
    {
      var rrule = data.RootElement.GetProperty(rruleField!).GetString();
      var tz = tzField is not null && data.RootElement.TryGetProperty(tzField, out var tzv) && tzv.ValueKind == JsonValueKind.String
        ? tzv.GetString() : null;
      var obj = new JsonObject { ["start"] = at, ["rrule"] = rrule };
      if (tz is not null)
      {
        obj["tz"] = tz;
      }

      return obj.ToJsonString();
    }

    return new JsonObject { ["at"] = at }.ToJsonString();
  }
}
```

- [ ] **Step 4: Run, confirm 3 PASS.**

- [ ] **Step 5: Wire copy-down into `EntityRepository.CreateAsync`.** Add an optional provisioner param and call it after create. In `src/toimi.tools.tietue/Entities/EntityRepository.cs`:
  - add `using toimi.tools.tietue.Provisioning;`
  - change the class declaration to: `public class EntityRepository(TietueDbContext db, SchemaValidator validator, BehaviorDispatcher? dispatcher = null, TriggerProvisioner? provisioner = null)`
  - In `CreateAsync`, the type is looked up for its schema. Load the full `TypeDefinition` so its `DefaultTriggers` is available. Replace the `GetSchemaOrThrowAsync(type, ct)` call site with a load of the `TypeDefinition` entity:
    ```csharp
    var typeDef = await db.TypeDefinitions.FirstOrDefaultAsync(t => t.Name == type, ct)
      ?? throw new TietueValidationException([$"Unknown type '{type}'. Define it first with define_type."]);
    Validate(typeDef.JsonSchema.RootElement.GetRawText(), data);
    ```
    (Keep the private `GetSchemaOrThrowAsync` for `UpdateAsync`'s use unchanged.)
  - After the existing dispatcher dispatch block (and before `return entity;`), add:
    ```csharp
    if (provisioner is not null)
    {
      await provisioner.ProvisionAsync(entity, typeDef.DefaultTriggers, entity.CreatedAt, ct);
    }
    ```
  Existing tests that build `new EntityRepository(db, validator)` or `(db, validator, dispatcher)` still compile (provisioner defaults null → no triggers).

- [ ] **Step 6: `define_type` gains `defaultTriggers`.** In `src/toimi.tools.tietue/Types/TypeRepository.cs`, extend `DefineAsync` with a `string? defaultTriggersJson = null` parameter (validate it parses as JSON when provided, like `behaviorsJson`; store on create + update). In `src/toimi.tools.tietue/Tools/DefineTypeTool.cs`, add a `[Description("Optional JSON array of default triggers stamped onto new entities")] string? defaultTriggers = null` parameter and pass it through to `DefineAsync(name, schema, behaviors, defaultTriggers)`.
  > Match the exact pattern already used for `behaviorsJson` (the prior phase added it); the `DefineAsync` signature becomes `DefineAsync(string name, string schemaJson, string? behaviorsJson = null, string? defaultTriggersJson = null, CancellationToken ct = default)`.

- [ ] **Step 7: Run the FULL suite** — existing tests still pass; provisioner tests pass. Confirm `TypeRepositoryTests`/`TypeToolsTests` (which call `DefineAsync`/`DefineType` with fewer args) still compile and pass.

- [ ] **Step 8: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Provisioning src/toimi.tools.tietue/Entities/EntityRepository.cs src/toimi.tools.tietue/Types/TypeRepository.cs src/toimi.tools.tietue/Tools/DefineTypeTool.cs src/toimi.tools.tietue.Tests/TriggerProvisionerTests.cs
git commit -m "feat(tietue): copy-down default triggers on entity create"
```

---

## Task 12: MCP tools — triggers + complete_occurrence

**Files:**
- Create: `src/toimi.tools.tietue/Tools/SetTriggerTool.cs`, `UpdateTriggerTool.cs`, `DeleteTriggerTool.cs`, `ListTriggersTool.cs`, `CompleteOccurrenceTool.cs`
- Test: `src/toimi.tools.tietue.Tests/TriggerToolsTests.cs`

- [ ] **Step 1: Failing tests.** `src/toimi.tools.tietue.Tests/TriggerToolsTests.cs`:
```csharp
using System.Text.Json;
using toimi.tools.tietue.Events;
using toimi.tools.tietue.Scheduling;
using toimi.tools.tietue.Tools;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class TriggerToolsTests
{
  [Fact]
  public async Task SetTrigger_then_ListTriggers_includes_it()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var entityId = Guid.NewGuid();

    var set = await new SetTriggerTool(repo).SetTrigger(entityId.ToString(), """{"at":"2026-06-20T09:00:00Z"}""", "notify", """{"titleTemplate":"hi"}""");
    Assert.Contains("\"id\"", set);

    var list = await new ListTriggersTool(repo).ListTriggers(entityId.ToString());
    using var doc = JsonDocument.Parse(list);
    Assert.Equal(1, doc.RootElement.GetArrayLength());
  }

  [Fact]
  public async Task DeleteTrigger_removes_it()
  {
    using var db = TestDb.New();
    var repo = new TriggerRepository(db);
    var t = await repo.CreateAsync(Guid.NewGuid(), """{"at":"2026-06-20T09:00:00Z"}""", "notify", null, DateTimeOffset.UtcNow);

    Assert.Contains("deleted", await new DeleteTriggerTool(repo).DeleteTrigger(t.Id.ToString()));
  }

  [Fact]
  public async Task CompleteOccurrence_records_completion()
  {
    using var db = TestDb.New();
    var store = new EntityEventStore(db);
    var entityId = Guid.NewGuid();

    var result = await new CompleteOccurrenceTool(store).CompleteOccurrence(entityId.ToString(), "2026-06-20T09:00:00Z");

    Assert.Contains("completed", result);
    Assert.True(await store.OccurrenceHandledAsync(entityId, new DateTimeOffset(2026, 6, 20, 9, 0, 0, TimeSpan.Zero), "notify"));
  }
}
```

- [ ] **Step 2: Run, confirm FAIL.**

- [ ] **Step 3: Implement the five tools.**

`src/toimi.tools.tietue/Tools/SetTriggerTool.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SetTriggerTool(TriggerRepository repository)
{
  [McpServerTool, Description("Schedule a trigger on an entity. 'schedule' is JSON: {\"at\":\"<iso utc>\"} for one-shot, or {\"start\":\"<iso utc>\",\"rrule\":\"FREQ=...\",\"tz\":\"Europe/Helsinki\"} for recurring (RFC 5545). 'handlerKind' is 'notify' or 'set-field'; 'handlerConfig' is its JSON config.")]
  public async Task<string> SetTrigger(
      [Description("Entity id (GUID)")] string entityId,
      [Description("Schedule spec JSON")] string schedule,
      [Description("Handler kind: notify | set-field")] string handlerKind,
      [Description("Handler config JSON (optional)")] string? handlerConfig = null)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    var t = await repository.CreateAsync(id, schedule, handlerKind, handlerConfig, DateTimeOffset.UtcNow);
    return JsonSerializer.Serialize(new { id = t.Id.ToString(), nextFireAt = t.NextFireAt?.ToString("o") });
  }
}
```

`src/toimi.tools.tietue/Tools/UpdateTriggerTool.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class UpdateTriggerTool(TriggerRepository repository)
{
  [McpServerTool, Description("Update a trigger's schedule, handler config, and/or enabled flag.")]
  public async Task<string> UpdateTrigger(
      [Description("Trigger id (GUID)")] string id,
      [Description("New schedule spec JSON (optional)")] string? schedule = null,
      [Description("New handler config JSON (optional)")] string? handlerConfig = null,
      [Description("Enable/disable the trigger (optional)")] bool? enabled = null)
  {
    if (!Guid.TryParse(id, out var triggerId))
    {
      return "Invalid id. Expected a GUID.";
    }

    var t = await repository.UpdateAsync(triggerId, schedule, handlerConfig, enabled, DateTimeOffset.UtcNow);
    return t is null
      ? $"Trigger '{id}' not found."
      : JsonSerializer.Serialize(new { id = t.Id.ToString(), enabled = t.Enabled, nextFireAt = t.NextFireAt?.ToString("o") });
  }
}
```

`src/toimi.tools.tietue/Tools/DeleteTriggerTool.cs`:
```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DeleteTriggerTool(TriggerRepository repository)
{
  [McpServerTool, Description("Delete a trigger by id.")]
  public async Task<string> DeleteTrigger(
      [Description("Trigger id (GUID)")] string id)
  {
    if (!Guid.TryParse(id, out var triggerId))
    {
      return "Invalid id. Expected a GUID.";
    }

    return await repository.DeleteAsync(triggerId) ? $"Trigger '{id}' deleted." : $"Trigger '{id}' not found.";
  }
}
```

`src/toimi.tools.tietue/Tools/ListTriggersTool.cs`:
```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Scheduling;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListTriggersTool(TriggerRepository repository)
{
  [McpServerTool, Description("List the triggers on an entity.")]
  public async Task<string> ListTriggers(
      [Description("Entity id (GUID)")] string entityId)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    var triggers = await repository.ListByEntityAsync(id);
    var rows = triggers.Select(t => new JsonObject
    {
      ["id"] = t.Id.ToString(),
      ["schedule"] = JsonNode.Parse(t.Schedule),
      ["handlerKind"] = t.HandlerKind,
      ["enabled"] = t.Enabled,
      ["nextFireAt"] = t.NextFireAt?.ToString("o"),
    }).ToArray();
    return JsonSerializer.Serialize(new JsonArray(rows));
  }
}
```

`src/toimi.tools.tietue/Tools/CompleteOccurrenceTool.cs`:
```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Events;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class CompleteOccurrenceTool(EntityEventStore events)
{
  [McpServerTool, Description("Mark a specific occurrence of an entity's trigger as completed, so it won't fire (or fire again). Provide the occurrence's UTC time (ISO 8601). For a one-shot reminder this is its scheduled time.")]
  public async Task<string> CompleteOccurrence(
      [Description("Entity id (GUID)")] string entityId,
      [Description("Occurrence time, ISO 8601 UTC")] string occurrenceUtc)
  {
    if (!Guid.TryParse(entityId, out var id))
    {
      return "Invalid entityId. Expected a GUID.";
    }

    if (!DateTimeOffset.TryParse(occurrenceUtc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var occ))
    {
      return "Invalid occurrenceUtc. Use ISO 8601 (e.g. 2026-06-20T09:00:00Z).";
    }

    await events.CompleteAsync(id, occ);
    return $"Occurrence {occurrenceUtc} completed.";
  }
}
```

- [ ] **Step 4: Run, confirm 3 PASS + full suite green.**

- [ ] **Step 5: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Tools/SetTriggerTool.cs src/toimi.tools.tietue/Tools/UpdateTriggerTool.cs src/toimi.tools.tietue/Tools/DeleteTriggerTool.cs src/toimi.tools.tietue/Tools/ListTriggersTool.cs src/toimi.tools.tietue/Tools/CompleteOccurrenceTool.cs src/toimi.tools.tietue.Tests/TriggerToolsTests.cs
git commit -m "feat(tietue): add trigger + complete-occurrence MCP tools"
```

---

## Task 13: Seed the `reminder` standard type

**Files:**
- Modify: `src/toimi.tools.tietue/Seed/TypeSeeder.cs`
- Test: `src/toimi.tools.tietue.Tests/TypeSeederTests.cs` (extend)

- [ ] **Step 1: Extend the seeder.** In `src/toimi.tools.tietue/Seed/TypeSeeder.cs`, change the standard-types data to a 4-tuple including default triggers, and seed `reminder`. The `StandardTypes` array entries become `(string Name, string Schema, string? Behaviors, string? DefaultTriggers)`, and `SeedAsync` calls `repository.DefineAsync(name, schema, behaviors, defaultTriggers, ct)`. Update `memory` and `skill` entries to pass `null` for the new default-triggers slot, and add:
```csharp
    (
      "reminder",
      """
      {"type":"object","properties":{
        "title":{"type":"string","description":"what to be reminded about"},
        "description":{"type":"string"},
        "dueAt":{"type":"string","description":"first occurrence, ISO 8601 UTC"},
        "timezone":{"type":"string","description":"IANA tz, e.g. Europe/Helsinki"},
        "rrule":{"type":"string","description":"optional RFC 5545 RRULE for recurrence"}
      },"required":["title","dueAt"]}
      """,
      null,
      """
      [{"when":{"atField":"dueAt","rruleField":"rrule","tzField":"timezone"},
        "handler":{"kind":"notify","config":{"titleTemplate":"{title}","messageTemplate":"{description}","tags":"bell"}}}]
      """
    ),
```

- [ ] **Step 2: Extend the seeder tests.** In `src/toimi.tools.tietue.Tests/TypeSeederTests.cs`, add:
```csharp
  [Fact]
  public async Task Seeds_reminder_with_default_notify_trigger()
  {
    using var db = TestDb.New();
    var repo = new TypeRepository(db);

    await new TypeSeeder(repo).SeedAsync();

    var reminder = await repo.GetAsync("reminder");
    Assert.NotNull(reminder);
    Assert.Contains("notify", reminder!.DefaultTriggers!);
    Assert.Contains("dueAt", reminder.DefaultTriggers!);
  }
```
Also update the existing `Seeding_twice_is_idempotent` assertion if it checks a specific count: it should now expect **3** types (`memory`, `skill`, `reminder`).

- [ ] **Step 3: Run the seeder tests + full suite, confirm green.**

- [ ] **Step 4: Format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Seed/TypeSeeder.cs src/toimi.tools.tietue.Tests/TypeSeederTests.cs
git commit -m "feat(tietue): seed reminder standard type with default notify trigger"
```

---

## Task 14: Program wiring + ntfy config + deployment env

**Files:**
- Modify: `src/toimi.tools.tietue/Program.cs`, `appsettings.json`, `k8s/base/tools-tietue/deployment.yaml`

- [ ] **Step 1: ntfy config in appsettings.** In `src/toimi.tools.tietue/appsettings.json`, add a top-level section:
```json
  "Ntfy": {
    "BaseUrl": "http://localhost:8080",
    "Topic": "toimi"
  }
```

- [ ] **Step 2: Register everything in `Program.cs`.** Add the `using`s and registrations. Mirror muistutin for ntfy. After the existing scoped repository/dispatcher registrations and before `AddMcpServer`, add:
```csharp
var ntfyOptions = builder.Configuration.GetSection("Ntfy").Get<Toimi.Notifications.NtfyOptions>() ?? new Toimi.Notifications.NtfyOptions();
builder.Services.AddSingleton(new Toimi.Notifications.NtfyClient(ntfyOptions));
builder.Services.AddSingleton<toimi.tools.tietue.Notifications.INotifier, toimi.tools.tietue.Notifications.NtfyNotifier>();

builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.NotifyHandler>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.INativeHandler, toimi.tools.tietue.Handlers.SetFieldHandler>();
builder.Services.AddScoped<toimi.tools.tietue.Handlers.HandlerRegistry>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.TriggerRepository>();
builder.Services.AddScoped<toimi.tools.tietue.Provisioning.TriggerProvisioner>();
builder.Services.AddScoped<toimi.tools.tietue.Events.EntityEventStore>();
builder.Services.AddScoped<toimi.tools.tietue.Scheduling.SchedulerTick>();
builder.Services.AddHostedService<toimi.tools.tietue.Scheduling.TriggerWorker>();
```
> IMPORTANT lifetime note: register BOTH `INativeHandler` implementations as **scoped** (as above). `SetFieldHandler` depends on the scoped `EntityRepository`, so it must be scoped; `NotifyHandler` depends on the singleton `INotifier`, which is fine to consume from a scoped registration. The scoped `HandlerRegistry` then resolves `IEnumerable<INativeHandler>` within a scope with no captive-dependency error. The `TriggerWorker` (singleton hosted service) creates a scope per tick (`IServiceScopeFactory`) and resolves the scoped `SchedulerTick` from it. Verify the app builds and a scope resolves the registry.

- [ ] **Step 3: deployment env.** In `k8s/base/tools-tietue/deployment.yaml`, add the four ntfy secret env entries (mirroring `k8s/base/tools-muistutin/deployment.yaml`), under the existing `env:` list:
```yaml
            - name: Ntfy__BaseUrl
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: ntfy-base-url
            - name: Ntfy__Topic
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: ntfy-topic
            - name: Ntfy__Username
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: ntfy-username
            - name: Ntfy__Password
              valueFrom:
                secretKeyRef:
                  name: toimi-secrets
                  key: ntfy-password
```

- [ ] **Step 4: Run the FULL suite** (the hosted service + DI are exercised by the `AdminEndpointsTests` booting the real `Program` under the in-memory provider — `IsRelational()` false skips the migrate/seed, and the worker's first tick either no-ops or is harmless within the test lifetime). Confirm all pass. Then build to confirm DI composes:
`docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj`
> If the hosted `TriggerWorker` interferes with the in-memory admin tests (e.g. throws because no Qdrant/real DB), follow muistutin's test pattern: in `TietueTestFactory.ConfigureWebHost`, remove the hosted service descriptor (`services.RemoveAll<IHostedService>()` for the `TriggerWorker`, mirroring how muistutin's test removes `ReminderNotifier`). Add that removal if needed so tests stay deterministic.

- [ ] **Step 5: Validate YAML + format + commit.**
```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c 'python3 -c "import yaml; yaml.safe_load(open(\"k8s/base/tools-tietue/deployment.yaml\")); print(\"YAML_OK\")"; dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj; dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj'
git add src/toimi.tools.tietue/Program.cs src/toimi.tools.tietue/appsettings.json k8s/base/tools-tietue/deployment.yaml src/toimi.tools.tietue.Tests/AdminEndpointsTests.cs
git commit -m "feat(tietue): wire handlers, scheduler worker, ntfy, and deployment env"
```

---

## Task 15: Full-suite verification

**Files:** none (verification only)

- [ ] **Step 1: tietue suite + lint (real exit codes).**
```
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes; echo "MAIN=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes; echo "TESTS=$?"
'
```
Expected: all tests pass (Phases 1–2 plus the new Phase 3 tests), `MAIN=0`, `TESTS=0`.

- [ ] **Step 2: Manual smoke test against real Postgres + ntfy (optional but recommended).** With a `tietue` Postgres DB and an ntfy server reachable: run the server, create a `reminder` entity with `dueAt` a minute in the future, confirm a `notify` trigger was copy-down provisioned (`list_triggers`), wait for the worker tick, confirm the ntfy notification arrives and an `entity_events` row (`kind=notify`) was recorded. Then create a recurring reminder (with `rrule`), confirm it reschedules. Then `complete_occurrence` and confirm it stops firing.

- [ ] **Step 3: Final commit if anything changed.**
```bash
git add -A && git commit -m "chore(tietue): phase 3 triggers + scheduler complete" --allow-empty
```

---

## Phase 3 Done — what exists now

`tietue` is now reactive: entities carry **triggers**, a background **scheduler** fires them on time, **native handlers** (`notify`, `set-field`) perform deterministic actions, and every firing is logged to **`entity_events`** (with completion suppressing re-fires). Types declare **default triggers** that are copied onto each new entity, and the seeded **`reminder`** type means reminders work end-to-end through `tietue` — functionally replacing `muistutin` (its pod retires at the Phase 6 cutover).

**Deferred (noted inline):** the `poll-diff` handler (the "Watch" capability — needs HTTP fetch + extraction), the `activate` MCP verb (pairs with Phase 4's message handler), DST-aware recurrence (UTC-start expansion, same as muistutin), and rules sparser than the 2-year forward window.

**Next phases (separate plans):**
- **Phase 4** — the `message` handler (ephemeral + lazy threaded conversations), the entity inbox + `activate` verb, self-scheduling agents; seed `schedule`. Retires ajastin.
- **Phase 5** — the sandboxed `script` handler + escalation.
- **Phase 6** — cutover: delete muistio/taidot/muistutin/ajastin pods, DBs, k8s bases; update standard-skill seeds + MCP URLs.
```
