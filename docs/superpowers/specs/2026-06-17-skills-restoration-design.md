# Skills Restoration — Design

> **Status:** Design spec. Restores the standard skills (deleted with `taidot`)
> as tietue `skill` entities and re-enables skill catalog injection. Feeds a
> later implementation plan. Companion to the tietue consolidation
> (`2026-06-14-generic-entity-component-engine-design.md`).

## 1. Problem

The cutover to tietue deleted `taidot` and its 13 seeded standard skills. On
`feat/unified-model`:

- The `skill` **type** is seeded (`SemanticIndex` on description+instructions,
  `UniqueName` on name) but **no skill instances** exist.
- `list_skills` is gone (it was taidot's), yet both `ToimiHub.cs:26` and
  `AgentRunner.cs:19` still call `CallToolAsync("list_skills")` — it always
  returns null, so the "Available skills…" block is silently dropped. Skill
  catalog injection is effectively **dead**.

Net: the shipped procedural knowledge (briefings, list/journal management,
home inventory, displays, monitoring) is missing, and the agent no longer sees
a skills list at session start.

## 2. Goals

1. Re-seed the standard skills as tietue `skill` entities (idempotent).
2. Re-enable skill catalog injection with no host-code churn.
3. Rewrite each skill's instructions to the **tietue generic surface**
   (`create`/`update`/`search`/`list`/`set_trigger`/`complete_occurrence` over
   typed entities) instead of the deleted `SaveMemory`/`CreateReminder`/
   `CreateSchedule`/`SaveSkill` verbs.

## 3. Non-goals

- No new tool server; skills remain tietue `skill` entities.
- No change to the `skill` type schema (`name`, `description`, `instructions`;
  required `name`+`description`+`instructions`). Tags ride the entity's
  top-level `Tags`, not a schema field.
- No read-time changes to search/list. Skills are discovered via the existing
  generic `search`/`list` and the `list_skills` catalog tool.

## 4. Design

### 4.1 Component A — `list_skills` MCP tool (tietue)

A thin convenience/catalog tool in `src/toimi.tools.tietue/Tools/`: returns the
`skill` entities projected to `[{ name, description }]` (JSON). It is a
projection over `list type=skill` — no new storage. This **revives the existing
dead `CallToolAsync("list_skills")`** in `ToimiHub` and `AgentRunner` with zero
host changes, restoring the "Available skills (use search/get for full
instructions)" injection. Returns an empty array (not null) when there are no
skills; injection degrades gracefully.

> Tension acknowledged: tietue's MCP surface is otherwise generic verbs. A
> skill-specific catalog tool is justified because (a) catalog injection is
> skill-specific by nature, and (b) CLAUDE.md's catalog-injection design note
> already anticipates a `list_skills` call ("…and, if present, `list_skills`").
> It stays a thin projection, not bespoke logic.

### 4.2 Component B — `SkillSeeder` (tietue)

Runs at startup **after `TypeSeeder`** (same `Program.cs` migrate/seed block,
relational-only). Upserts the standard skills as `skill` entities **via
`EntityRepository`** so each is schema-validated, embedded by `SemanticIndex`,
and subject to `UniqueName`.

- **Upsert by name:** load all `skill` entities, map by `name`
  (case-insensitive); for each standard skill, **create if absent, else update**
  its `description`/`instructions`/tags. This keeps standard skills current
  across deploys (matches taidot's prior upsert behavior).
- **Idempotent:** re-seeding does not duplicate (UniqueName + the by-name map).
- **Trade-off:** a user editing a *standard-named* skill will have it
  overwritten on the next deploy. Documented expectation: fork under a new name
  to customize. (Acceptable for single-user; revisit if it bites.)
- **Tags:** passed as the entity's `Tags`.
- **Maintainer doc comment:** like the old seeder, carry a header listing the
  current tool servers + verbs so skills are kept in sync when tools change.

### 4.3 Component C — ported skill content

Each skill's instructions are rewritten against the current surface. **The full
rewritten text is authored in the implementation plan**; this spec fixes the
*set*, each skill's *intent*, and the *verb mapping* so the plan is unambiguous.

**Verb/translation map (old → tietue):**

| Old (deleted) | tietue |
|---|---|
| `SaveMemory(content,category,tags,source,confirmed,expiresAt?)` | `create type=memory` with `data={content,category,source,confirmed,expiresAt?}`, `tags=[…]` |
| `RecallMemory(query,…)` | `search type=memory query=…` (no category filter — filter by `tag` via `list`, or judge in-context) |
| `UpdateMemory(id,…)` | `update id data=…` |
| `ForgetMemory(id)` | `delete id` |
| `ListMemories(category,tags)` | `list type=memory` (supports `type`+`tag`; no category param) |
| `CreateReminder(title,dateTimeUtc,timeZone,rrule?)` | `create type=reminder` with `{title,dueAt,timezone,rrule?}` (notify trigger auto-provisioned) |
| `ListReminders(from,to)` | `list type=reminder` + `list_triggers` (no occurrence-window expansion) |
| `CompleteReminder(id,occUtc?)` | `complete_occurrence` |
| `DeleteReminder(id)` | `delete id` |
| `CreateSchedule(name,prompt,cron|runAt)` | `create type=schedule` with `{name,prompt,startAt,rrule?}` (message trigger auto-provisioned; **cron→RRULE**) |
| `ListSchedules` | `list type=schedule` |
| `Enable/DisableSchedule` | `update_trigger` (enabled) / `activate` |
| `DeleteSchedule` | `delete id` |
| `SaveSkill / GetSkill / FindSkill / ListSkills / DeleteSkill` | `create`/`update` / `get`+`search` / `search type=skill` / `list type=skill` (or `list_skills`) / `delete` |
| `FetchUrl` (verkko) | verkko fetch tool (verify exact current name) |
| `SendNotification` (verkko) | verkko ntfy tool (verify exact name) |
| `ListEntities` (koti) | unchanged (koti) |
| `DisplayList/Show/Overlay/Clear/GetEvents/CreateTemplate` (old ruutu) | current ruutu tools — `display_list_templates`, `display_show`, `display_overlay`, `display_clear`, `display_events`, `create_template`, `render`, `get_tier_brief` (verify exact names) |

> **Implementation prerequisite:** before authoring instructions, the
> implementer MUST read the *current* tool surfaces of verkko, koti, and ruutu
> and use their exact tool names — do not copy old names from this table.

**The set (12 skills):**

Ported, rewritten to tietue verbs:
1. **save-user-preference** — search memory for a conflicting preference, delete it, `create` a `memory` (category `preference`, source `user`, confirmed). (Note: the new "search-before-create" prompt guidance complements this.)
2. **daily-briefing** — list today's reminders (`list type=reminder` + triggers) and `search type=memory` for relevant context/preferences; compile a concise summary.
3. **weekly-review** — reminders + `list type=memory` (week) + `list type=schedule`; summarize and suggest cleanup.
4. **learn-and-remember** — `create` a `memory` with proper metadata; if the teaching is a repeatable procedure, also `create` a `skill`.
5. **cleanup-memories** — resolve stale/unconfirmed/conflicting memories. **Drop the "delete expired" step** — `Expiry` now auto-deletes expired memories; focus on `source=inferred` staleness and conflicts.
6. **monitor-url** — verkko fetch → `create` a `memory` (category `monitor`, tagged) → `create` a `schedule` whose prompt re-fetches, compares to the stored memory, notifies on change, and `update`s the memory. (Rewrite the embedded scheduled-prompt to tietue verbs.)
7. **manage-list** — markdown checklists as `memory` entities; **key each list by a `tag`** (the list name) and retrieve via `list type=memory tag=<name>` (since `list` filters by tag, not category). add/show/check/remove/clear/delete via `update`/`delete`.
8. **manage-journal** — journal entries as `memory` entities tagged by ISO date; add/recall/summarize/delete via `create`/`list`/`search`/`delete`.
9. **home-inventory** — reference skill (unchanged intent); resolve device names→entity ids/rooms; points to update-home-inventory when stale.
10. **update-home-inventory** — koti `ListEntities` → group by area → format → `update` the `home-inventory` skill entity (`create`/`update type=skill`).

New / folded:
11. **scheduling** (folds `set-recurring-reminder` + `schedule-task`) — when to create a `reminder` (notification) vs a `schedule` (agent run); RRULE patterns (daily/weekly/monthly/yearly), timezones (default Europe/Helsinki), writing self-contained prompts; note that creating the entity auto-provisions its trigger and that ajastin's cron is replaced by RFC 5545 RRULE.

Freshly authored (NOT ported — old version names dead tools):
12. **use-displays** — authored against the current ruutu surface: discover shapes via `display_list_templates`; `display_show` for the scene vs `display_overlay` for transient cards (notification template); `webview` template for embedding a page; the modern/legacy tier model and `create_template`/`get_tier_brief` for new shapes; the tap-back loop via `display_events`. Captures the *flow + conventions*, which the (rich) per-tool descriptions do not assemble.

## 5. Testing

- **SkillSeederTests:** seeds the full set as `skill` entities; idempotent
  (re-seed → count stable, no duplicates); upsert updates changed
  content/tags. Construct the seeder's `EntityRepository` **without** a
  `BehaviorDispatcher` (or with a fake index) so seeding does not require Qdrant
  — mirrors how existing repository tests avoid real embedding.
- **`list_skills` tool test:** returns `{name,description}` for seeded skills;
  empty array when none.
- **Injection:** `InitialMessagesTests` already verifies `skillSummary` is
  injected when provided; no change needed there. Optionally assert the
  `list_skills` projection shape matches what `CreateInitialMessages` expects.
- Existing 137 tests must stay green.

## 6. Ops / rollout

- No DB migration (skills are entities). On deploy, tietue startup seeds/upserts
  them and embeds them into the `skill` Qdrant collection. Existing user skills
  are untouched unless they share a standard name.

## 7. Risks

- **Instruction accuracy:** the rewrites must reference *real, current* tool
  names; stale names would mislead the agent (this is exactly the bug being
  fixed). Mitigation: §4.3 prerequisite — read the live tool surfaces first.
- **Search lacks category filtering:** several old skills filtered memory by
  `category`. tietue keys retrieval by `tag` or semantic `search`; the rewrites
  must use tags (list-keyed) where the old ones used category, and store
  `category` in `data` for context only.
- **Standard-skill overwrite on deploy** (§4.2) — documented expectation.
