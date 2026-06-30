# Skills Restoration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Restore the standard skills as tietue `skill` entities and re-enable skill catalog injection, with all instructions rewritten to the current tietue/verkko/koti/ruutu tool surface.

**Architecture:** (A) a `list_skills` MCP tool in tietue (projects `skill` entities → `[{name,description}]`) which revives the already-present `CallToolAsync("list_skills")` in `ToimiHub`/`AgentRunner` — verified: the MCP SDK snake_cases method names, so a `ListSkills` method ⇒ tool name `list_skills`. (B) a `SkillSeeder` that upserts standard skills as `skill` entities via `EntityRepository` at startup after `TypeSeeder`. (C) 12 skills authored against the current surface.

**Tech Stack:** .NET 10, EF Core 10, xUnit + EF InMemory, ModelContextProtocol 1.1.0.

**Conventions:** 2-space indent, file-scoped namespaces, block bodies, conditional expressions (IDE0046), no unused usings, no redundant `!` after `Assert.NotNull` (IDE0370). `/*lang=json,strict*/` before JSON string literals in tests. BOTH `dotnet format --verify-no-changes` checks (src + tests csproj) must exit 0. Build/test only via Docker: `docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 <cmd>`. Commit messages end with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`. Keep tree clean.

**Branch:** `feat/tietue-skills` off `feat/unified-model`; commit this plan there first.

### Allowed tool names (skills MUST reference only these — exact snake_case)
- tietue: `create`, `get`, `update`, `delete`, `list`, `search`, `define_type`, `list_types`, `get_type`, `delete_type`, `set_trigger`, `update_trigger`, `delete_trigger`, `list_triggers`, `complete_occurrence`, `activate`, `list_skills`
- verkko: `fetch_url`, `send_notification`
- koti: `list_entities`, `get_entity_state`, `call_service`, `get_history`
- ruutu: `display_list`, `display_list_templates`, `display_show`, `display_overlay`, `display_clear`, `display_get_events`, `display_get_template`, `display_create_template`, `display_update_template`, `display_preview`, `display_get_tier_brief`

---

## Task 1: `list_skills` MCP tool

**Files:** Create `src/toimi.tools.tietue/Tools/ListSkillsTool.cs`; Create `src/toimi.tools.tietue.Tests/ListSkillsToolTests.cs`.

- [ ] **Step 1: Failing test** — `ListSkillsToolTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Tools;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ListSkillsToolTests
{
  [Fact]
  public async Task Lists_name_and_description_for_skill_entities()
  {
    using var db = TestDb.New();
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    var entities = new EntityRepository(db, new SchemaValidator());
    await entities.CreateAsync("skill", JsonNode.Parse("""{"name":"s1","description":"d1","instructions":"i1"}"""), []);

    var json = await new ListSkillsTool(db).ListSkills();
    using var doc = JsonDocument.Parse(json);
    var first = doc.RootElement.EnumerateArray().Single();
    Assert.Equal("s1", first.GetProperty("name").GetString());
    Assert.Equal("d1", first.GetProperty("description").GetString());
  }

  [Fact]
  public async Task Empty_array_when_no_skills()
  {
    using var db = TestDb.New();
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    var json = await new ListSkillsTool(db).ListSkills();
    using var doc = JsonDocument.Parse(json);
    Assert.Empty(doc.RootElement.EnumerateArray());
  }
}
```

- [ ] **Step 2: Run, confirm compile failure** (`ListSkillsTool` missing).

- [ ] **Step 3: Implement** `Tools/ListSkillsTool.cs` (mirrors `ListTypesTool`):

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class ListSkillsTool(TietueDbContext db)
{
  [McpServerTool, Description("List available skills (reusable procedures) with their names and descriptions. Use search type='skill' or get for the full instructions of a skill.")]
  public async Task<string> ListSkills()
  {
    var skills = await db.Entities.Where(e => e.Type == "skill").OrderBy(e => e.CreatedAt).ToListAsync();
    var rows = skills.Select(e =>
    {
      var root = e.Data.RootElement;
      return new JsonObject
      {
        ["name"] = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null,
        ["description"] = root.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
      };
    }).ToArray();
    return JsonSerializer.Serialize(new JsonArray(rows));
  }
}
```

- [ ] **Step 4: Run scoped tests + both lint** (`--filter "FullyQualifiedName~ListSkillsToolTests"`). Expected PASS, both 0.
- [ ] **Step 5: Commit** `git commit -m "feat(tietue): list_skills MCP tool reviving skill catalog injection"`

---

## Task 2: `SkillSeeder` + standard skill content

**Files:** Create `src/toimi.tools.tietue/Seed/SkillSeeder.cs`; Modify `src/toimi.tools.tietue/Program.cs`; Create `src/toimi.tools.tietue.Tests/SkillSeederTests.cs`.

- [ ] **Step 1: Failing test** — `SkillSeederTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Seed;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SkillSeederTests
{
  private static async Task<SkillSeeder> SetupAsync(Data.TietueDbContext db)
  {
    await new TypeSeeder(new TypeRepository(db)).SeedAsync();
    // EntityRepository WITHOUT a BehaviorDispatcher → no Qdrant embedding in tests.
    var entities = new EntityRepository(db, new SchemaValidator());
    return new SkillSeeder(db, entities);
  }

  [Fact]
  public async Task Seeds_all_standard_skills()
  {
    using var db = TestDb.New();
    var seeder = await SetupAsync(db);
    await seeder.SeedAsync();
    var count = await db.Entities.CountAsync(e => e.Type == "skill");
    Assert.Equal(12, count);
  }

  [Fact]
  public async Task Seeding_is_idempotent()
  {
    using var db = TestDb.New();
    var seeder = await SetupAsync(db);
    await seeder.SeedAsync();
    await seeder.SeedAsync();
    Assert.Equal(12, await db.Entities.CountAsync(e => e.Type == "skill"));
  }

  [Fact]
  public async Task Upsert_refreshes_changed_content()
  {
    using var db = TestDb.New();
    var seeder = await SetupAsync(db);
    await seeder.SeedAsync();
    var skill = await db.Entities.FirstAsync(e => e.Type == "skill");
    // Tamper with stored instructions, then re-seed and confirm it's restored.
    skill.Data = System.Text.Json.JsonSerializer.SerializeToDocument(new { name = ReadName(skill), description = "x", instructions = "tampered" });
    await db.SaveChangesAsync();
    await seeder.SeedAsync();
    var reloaded = await db.Entities.FirstAsync(e => e.Id == skill.Id);
    Assert.DoesNotContain("tampered", reloaded.Data.RootElement.GetProperty("instructions").GetString());
  }

  private static string ReadName(Data.Entity e) => e.Data.RootElement.GetProperty("name").GetString()!;
}
```

> Note: `Upsert_refreshes_changed_content` relies on the by-name match restoring content. If the tamper changes the name too, it would create a new skill — the test keeps the original name via `ReadName`.

- [ ] **Step 2: Run, confirm compile failure** (`SkillSeeder` missing).

- [ ] **Step 3: Implement** `Seed/SkillSeeder.cs`. Mechanism first, then the full `StandardSkills` array (verbatim below — do not abbreviate the instruction strings):

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Entities;

namespace toimi.tools.tietue.Seed;

// Standard skills, seeded as `skill` entities and upserted by name (idempotent).
// MAINTAINER NOTE: keep instructions in sync with the live tool surface.
// Current tool names: tietue create/get/update/delete/list/search,
// define_type/list_types/get_type/delete_type, set_trigger/update_trigger/
// delete_trigger/list_triggers, complete_occurrence, activate, list_skills;
// verkko fetch_url/send_notification; koti list_entities/get_entity_state/
// call_service/get_history; ruutu display_list/display_list_templates/
// display_show/display_overlay/display_clear/display_get_events/
// display_create_template/display_get_template/display_update_template/
// display_preview/display_get_tier_brief.
public class SkillSeeder(TietueDbContext db, EntityRepository entities)
{
  private static readonly (string Name, string Description, string Instructions, string[] Tags)[] StandardSkills =
  [
    (
      "save-user-preference",
      "Save a user preference to long-term memory with proper categorization",
      """
      When the user shares a preference (food, color, units, language, comfort settings):
      1. search type='memory' for a conflicting preference on the same topic.
      2. If one exists, update that memory instead of creating a duplicate.
      3. Otherwise create type='memory' with data {content, category:"preference", source:"user", confirmed:true} and tags naming the topic (e.g. "food", "units").
      4. Briefly confirm what you saved.
      """,
      ["memory", "preference"]
    ),
    (
      "daily-briefing",
      "Generate a daily briefing with reminders and important context",
      """
      For a morning summary:
      1. list type='reminder', then list_triggers on the relevant ones to see today's times (user timezone, default Europe/Helsinki).
      2. search type='memory' with query "important today" and with query "user preferences" for relevant context.
      3. Compile a short, actionable summary: today's reminders with times, then noteworthy context. Keep it brief.
      """,
      ["briefing", "daily"]
    ),
    (
      "weekly-review",
      "Generate a weekly summary of reminders, memories, and schedules",
      """
      For a weekly review:
      1. list type='reminder' and list type='schedule' to see active reminders and automations.
      2. list type='memory' to see what was saved recently.
      3. Summarize: recent and upcoming reminders, new memories, active schedules.
      4. Suggest cleanup — stale reminders, outdated preferences.
      """,
      ["review", "weekly"]
    ),
    (
      "learn-and-remember",
      "When the user teaches something, save it properly and consider making it a skill",
      """
      When the user teaches you something or corrects your behavior:
      1. create type='memory' with data {content, category: one of "preference"/"fact"/"context"/"correction", source: "user" if they stated it directly else "inferred", confirmed: true if stated directly else false}. For temporary facts, set data.expiresAt to an ISO 8601 UTC time — it is auto-deleted when it expires.
      2. If it describes a repeatable procedure, also create type='skill' with data {name, description, instructions}.
      3. Confirm what you learned and how you will apply it.
      """,
      ["memory", "learning"]
    ),
    (
      "cleanup-memories",
      "Resolve stale and conflicting memories",
      """
      Periodic memory maintenance (expired memories are auto-deleted, so focus on staleness and conflicts):
      1. list type='memory' and scan for unconfirmed (source:"inferred") or clearly outdated/redundant entries.
      2. search type='memory' on topics likely to have conflicting entries; keep the most recent confirmed version and delete the others.
      3. delete entries that are stale, redundant, or incorrect.
      4. Report what you removed and any conflicts you resolved.
      """,
      ["memory", "maintenance", "cleanup"]
    ),
    (
      "monitor-url",
      "Monitor a URL for changes and report when something changes",
      """
      To monitor a URL (prices, package status, status pages):
      1. fetch_url to get the current content.
      2. create type='memory' with data {content: a concise summary of the current state, category:"monitor", source:"system"} and tags identifying it (e.g. "price", "<product>").
      3. create type='schedule' with data {name, prompt, startAt (ISO 8601 UTC), rrule (e.g. "FREQ=HOURLY" for tracking, "FREQ=DAILY" for prices)}. The schedule auto-runs the prompt. Write it self-contained, for example: "fetch_url <URL>. list type='memory' with tag '<tag>' for the previous state. Compare. If something meaningful changed, update that memory and send_notification with the old→new change and the link. If unchanged, do nothing."
      4. Confirm what is monitored, how often, and what triggers a notification.
      """,
      ["monitoring", "automation", "fetch"]
    ),
    (
      "manage-list",
      "Manage markdown checklists (shopping, packing, todo) stored in memory",
      """
      Lists are markdown checklists stored as `memory` entities — one entity per list, tagged with the list name.
      - Find a list: list type='memory' with tag "<listname>".
      - Add an item: if the list exists, update it (append a "- [ ] item" line); otherwise create type='memory' with data {content:"# <Name>\n- [ ] item", category:"list", source:"user"} and tags ["<listname>"].
      - Show a list: present its markdown.
      - Check off an item: update, changing "- [ ] item" to "- [x] item".
      - Remove an item or clear completed: update with those lines removed.
      - Delete a list: delete the entity.
      - List all lists: list type='memory' (the list-name tags identify them).
      """,
      ["list", "checklist", "todo", "shopping"]
    ),
    (
      "manage-journal",
      "Manage a personal journal stored in memory",
      """
      Journal entries are `memory` entities with category "journal", tagged with the ISO date.
      - Add: create type='memory' with data {content:"<YYYY-MM-DD>: <entry>", category:"journal", source:"user"} and tags ["<YYYY-MM-DD>"].
      - Recall recent: list type='memory' with tag "<date>", or search type='memory' with a topic query.
      - Summarize a period: list type='memory' and summarize the matching dates.
      - Delete an entry: delete the entity.
      When the user shares something reflective or diary-like, offer to save it as a journal entry.
      """,
      ["journal", "notes", "diary"]
    ),
    (
      "home-inventory",
      "Reference information about the smart home: areas, devices, entity ids, and rooms",
      """
      This skill holds the home inventory — a room-organized map of device entity ids — used to resolve device names to entity ids and rooms.
      If this looks empty or outdated, run the "update-home-inventory" skill to refresh it. Until then, discover devices live with koti list_entities.
      """,
      ["home", "inventory", "reference"]
    ),
    (
      "update-home-inventory",
      "Refresh the home-inventory skill from Home Assistant",
      """
      To refresh the home inventory:
      1. koti list_entities (no filter) to get all entities with their areas.
      2. Group entities by area/room; for each device list entity_id, friendly name, domain, and current state (include unavailable ones).
      3. Format a clear reference document organized by room.
      4. Find the "home-inventory" skill (list type='skill', or get it) and update its instructions to this document (keep its name and description).
      5. Confirm the update and summarize what you found.
      """,
      ["home", "inventory", "maintenance"]
    ),
    (
      "scheduling",
      "Set up reminders (notifications) and schedules (agent runs), one-time or recurring",
      """
      Two ways to make something happen later — both auto-provision their trigger when you create the entity:
      - A REMINDER notifies the user. create type='reminder' with data {title, dueAt (ISO 8601 UTC), timezone (IANA, default Europe/Helsinki), rrule (optional RFC 5545)}. It sends a push notification when due — no separate notification needed.
      - A SCHEDULE runs an agent prompt. create type='schedule' with data {name, prompt, startAt (ISO 8601 UTC), rrule (optional)}.
      RRULE patterns: daily FREQ=DAILY; weekdays FREQ=WEEKLY;BYDAY=MO,WE,FR; monthly FREQ=MONTHLY;BYMONTHDAY=15; yearly FREQ=YEARLY. (RFC 5545 replaces cron.)
      Convert the user's local time to UTC. For a schedule, write a self-contained prompt — the run has no chat context; state exactly what to do and which tools to use. A run can reschedule itself with set_trigger. Confirm the time back in the user's timezone.
      """,
      ["schedule", "reminder", "automation"]
    ),
    (
      "use-displays",
      "Push content to user-owned web displays (wall tablets, old iPads) via ruutu",
      """
      Displays are physical screens you push content to with ruutu.
      1. display_list to see registered displays — each has an id (e.g. "kitchen"), a tier (modern/legacy, auto-detected), and online status.
      2. display_list_templates to see available shapes (name, description, data schema). Read this first; don't write raw HTML.
      3. Set the main scene: display_show(identifier, template, data matching its schema). To embed a web page or tracking link, use the "webview" template with {url:"https://…", title (optional)}. Only https URLs; pages that forbid framing (X-Frame-Options/CSP) render blank.
      4. Transient cards: display_overlay(identifier, template, data) — overlays stack and stay until tapped; the "notification" template is the usual choice.
      5. Reset to idle: display_clear(identifier).
      6. New shapes: display_create_template requires both modern_html and legacy_html (legacy = iOS Safari 9: no flexbox/grid, no CSS variables, no WebP — use tables/floats/system fonts); both are linted. display_get_tier_brief gives the full authoring rules; display_preview sanity-checks output before saving.
      7. Interaction: when the user taps something, display_get_events(identifier, since optional) returns tap-backs — poll it during an in-progress routine to track progress (taps do not auto-start a session).
      """,
      ["displays", "ruutu", "ui", "templates"]
    ),
  ];

  public async Task SeedAsync(CancellationToken ct = default)
  {
    var existing = await db.Entities.Where(e => e.Type == "skill").ToListAsync(ct);
    var byName = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in existing)
    {
      if (e.Data.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
      {
        byName[n.GetString()!] = e;
      }
    }

    foreach (var (name, description, instructions, tags) in StandardSkills)
    {
      var data = new JsonObject
      {
        ["name"] = name,
        ["description"] = description,
        ["instructions"] = instructions.Trim(),
      };

      if (byName.TryGetValue(name, out var found))
      {
        await entities.UpdateAsync(found.Id, data, tags, ct);
      }
      else
      {
        await entities.CreateAsync("skill", data, tags, ct);
      }
    }
  }
}
```

- [ ] **Step 4: Wire into `Program.cs`.** Register the seeder near the other scoped services (after the `TypeSeeder` registration ~line 40):

```csharp
builder.Services.AddScoped<toimi.tools.tietue.Seed.SkillSeeder>();
```

and in the startup seed block, call it right after `TypeSeeder.SeedAsync()`:

```csharp
    await scope.ServiceProvider.GetRequiredService<toimi.tools.tietue.Seed.TypeSeeder>().SeedAsync();
    await scope.ServiceProvider.GetRequiredService<toimi.tools.tietue.Seed.SkillSeeder>().SeedAsync();
```

> The DI-resolved `EntityRepository` has the `BehaviorDispatcher`, so seeded skills are embedded into the `skill` Qdrant collection at startup (the dispatcher self-ensures the collection). Order: types → skills.

- [ ] **Step 5: Run FULL suite + both lint.** Expected: all pass (137 prior + Task 1's 2 + these 3 = 142), both 0.
- [ ] **Step 6: Commit** `git commit -m "feat(tietue): seed standard skills as skill entities"`

---

## Task 3: Verification + skill-content accuracy review

- [ ] **Step 1:** Full suite + both lint:

```bash
docker run --rm -v /Users/jari/private/toimi:/work -w /work mcr.microsoft.com/dotnet/sdk:10.0 bash -c '
  dotnet test src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj 2>&1 | grep -E "Passed!|Failed!"
  dotnet format src/toimi.tools.tietue/toimi.tools.tietue.csproj --verify-no-changes >/dev/null 2>&1; echo "SRC=$?"
  dotnet format src/toimi.tools.tietue.Tests/toimi.tools.tietue.Tests.csproj --verify-no-changes >/dev/null 2>&1; echo "TESTS=$?"'
```
Expected: all pass, both 0.

- [ ] **Step 2: Tool-name accuracy check.** Verify every tool name referenced in `SkillSeeder.cs`'s instructions is in the "Allowed tool names" list at the top of this plan (no `SaveMemory`/`CreateReminder`/`CreateSchedule`/`SaveSkill`/`DisplayList`/`ListMemories`/old names). Grep for the dead names and confirm zero hits:

```bash
grep -nE "SaveMemory|RecallMemory|UpdateMemory|ForgetMemory|ListMemories|CreateReminder|ListReminders|CompleteReminder|CreateSchedule|ListSchedules|EnableSchedule|DisableSchedule|SaveSkill|GetSkill|FindSkill|ListSkills\(|FetchUrl|SendNotification|DisplayList\b|DisplayShow|DisplayOverlay|DisplayClear|DisplayGetEvents|DisplayListTemplates|DisplayCreateTemplate" src/toimi.tools.tietue/Seed/SkillSeeder.cs && echo "DEAD NAME FOUND" || echo "clean"
```
Expected: `clean`. (Note: the maintainer doc-comment lists current names like `fetch_url`/`list_entities`; those are fine. The grep targets the OLD PascalCase names.)

- [ ] **Step 3:** Hand back to the controller for the finishing-a-development-branch step.

---

## Notes
- The full rewritten instruction text is in this plan (Task 2) — transcribe verbatim; do not paraphrase or invent tool names.
- `list_skills` revives injection in both `ToimiHub` and `AgentRunner` with no host change (verified naming: `ListSkills` ⇒ `list_skills`).
- Skills are not retro-fixed for pre-existing user skills; seeding only manages the standard names.
