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
