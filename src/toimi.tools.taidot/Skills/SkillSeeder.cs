namespace toimi.tools.taidot.Skills;

/// <summary>
/// Seeds standard skills into the repository on startup.
/// Skills are upserted by name, so this is idempotent.
///
/// MAINTAINER NOTE: Update this file when new MCP tools become available.
/// Each skill should describe a reusable procedure using only currently
/// available tools. When a new tool server is added, review existing skills
/// and add new ones that leverage the new capabilities.
///
/// Available tool servers and their tools (as of 2026-04-01):
/// - muistio: SaveMemory, RecallMemory, UpdateMemory, ForgetMemory, ListMemories
/// - muistutin: CreateReminder, ListReminders, CompleteReminder, DeleteReminder
/// - taidot: SaveSkill, GetSkill, FindSkill, ListSkills, DeleteSkill
/// - ajastin: CreateSchedule, ListSchedules, DeleteSchedule, EnableSchedule, DisableSchedule
/// - koti: GetEntityState, ListEntities (with area filter), CallService, GetHistory (Home Assistant REST API)
/// - verkko: FetchUrl (fetch URL, extract text from HTML/JSON, 5-min cache), SendNotification (ntfy push notifications)
/// </summary>
public class SkillSeeder(SkillRepository repository, EmbeddingService embeddings)
{
  private static readonly (string Name, string Description, string Instructions, string[] Tags)[] StandardSkills =
  [
    (
      "save-user-preference",
      "Save a user preference to long-term memory with proper categorization",
      """
      When the user shares a preference (favorite color, food, temperature unit, language, etc.):
      1. Use RecallMemory to check if a conflicting preference already exists
      2. If a conflicting preference exists, use ForgetMemory to remove the old one first
      3. Use SaveMemory with:
         - category: "preference"
         - source: "user"
         - confirmed: true
         - tags: descriptive (e.g. "food", "color", "units", "language")
      4. Confirm to the user what was saved
      """,
      ["memory", "preference"]
    ),
    (
      "daily-briefing",
      "Generate a daily briefing with reminders and important context",
      """
      When asked for a daily briefing or morning summary:
      1. Use ListReminders to get today's reminders (from now to end of day in user's timezone)
      2. Use RecallMemory with query "important today" to find relevant context
      3. Use RecallMemory with query "user preferences" to recall communication preferences
      4. Compile a concise summary:
         - List today's reminders with times
         - Mention any relevant context from memory
      5. Keep it brief and actionable
      """,
      ["briefing", "daily"]
    ),
    (
      "set-recurring-reminder",
      "Create a recurring reminder with the correct RFC 5545 recurrence rule",
      """
      When the user wants a recurring reminder:
      1. Determine the recurrence pattern from the user's description
      2. Convert to RFC 5545 format:
         - Daily: FREQ=DAILY
         - Weekly on specific days: FREQ=WEEKLY;BYDAY=MO,WE,FR
         - Monthly on a date: FREQ=MONTHLY;BYMONTHDAY=15
         - Yearly: FREQ=YEARLY
      3. Ask for timezone if not obvious (default to Europe/Helsinki)
      4. Use CreateReminder with the ISO 8601 UTC datetime and recurrence rule
      5. Confirm the schedule to the user in their local time

      Note: Reminders automatically send push notifications when due. No need to set up separate notifications or schedules for reminder delivery.
      """,
      ["reminder", "recurring"]
    ),
    (
      "schedule-task",
      "Create a scheduled agent task — recurring (cron) or one-time (specific datetime)",
      """
      When the user wants something to run on a schedule or at a specific time:

      For RECURRING tasks:
      1. Determine the cron expression from the user's description:
         - "every morning at 7" → 0 7 * * *
         - "every hour" → 0 * * * *
         - "every Monday at 9" → 0 9 * * 1
         - "every day at midnight" → 0 0 * * *
      2. Note: cron times are in UTC. Convert from user's timezone (Europe/Helsinki = UTC+2/+3)
      3. Use CreateSchedule with name, prompt, and cronExpression

      For ONE-TIME tasks:
      1. Convert the user's desired time to UTC ISO 8601 format
      2. Use CreateSchedule with name, prompt, and runAt
      3. One-time schedules auto-disable after running

      For both:
      - Write a clear, self-contained prompt — the agent running it has no conversation context
      - The prompt should specify exactly what to do, including which tools to use
      - Confirm the schedule to the user, showing the run time in their timezone
      """,
      ["schedule", "automation"]
    ),
    (
      "weekly-review",
      "Generate a weekly summary of reminders and saved memories",
      """
      When asked for a weekly review or summary:
      1. Use ListReminders for the past 7 days to see what was scheduled
      2. Use ListMemories to see what was saved to memory this week
      3. Use ListSchedules to show active automated tasks
      4. Compile a summary:
         - Completed and upcoming reminders
         - New things learned/saved to memory
         - Active schedules and their recent activity
      5. Suggest any adjustments (remove old reminders, update preferences)
      """,
      ["review", "weekly"]
    ),
    (
      "learn-and-remember",
      "When the user teaches something, save it properly and consider if it should be a skill",
      """
      When the user explicitly teaches you something or corrects your behavior:
      1. Use SaveMemory with appropriate metadata:
         - category: "preference", "fact", "context", or "correction"
         - source: "user" if the user stated it directly, "inferred" if you deduced it
         - confirmed: true if user stated directly, false if inferred
         - For temporary context, set expiresAt to an appropriate future date
      2. If the teaching describes a repeatable procedure (how to do X), also save it as a skill:
         - Use SaveSkill with a descriptive name
         - Write clear step-by-step instructions
      3. Confirm what was learned and how it will be applied
      """,
      ["memory", "learning"]
    ),
    (
      "home-inventory",
      "Reference information about the smart home: areas, devices, entity IDs, and their rooms",
      """
      This skill contains the home inventory. Use it to resolve device names to entity IDs and rooms.
      If this skill's content seems outdated, use the "update-home-inventory" skill to refresh it.

      NOTE: This skill should be updated with actual data by running "update-home-inventory".
      Until then, use ListEntities to discover devices dynamically.
      """,
      ["home", "inventory", "reference"]
    ),
    (
      "update-home-inventory",
      "Update the home-inventory skill with current device and area information from Home Assistant",
      """
      To update the home inventory:
      1. Use ListEntities (no filters) to get ALL entities with their areas
      2. Group entities by area/room
      3. For each area, list the devices with their entity_id, friendly_name, domain, and current state
      4. Format as a clear reference document, organized by room, include all devices, even unavailable
      5. Use SaveSkill to update the "home-inventory" skill with this information:
         - name: "home-inventory"
         - description: "Reference information about the smart home: areas, devices, entity IDs, and their rooms"
         - tags: "home,inventory,reference"
         - instructions: the formatted inventory document
      6. Confirm the update was successful and summarize what was found
      """,
      ["home", "inventory", "maintenance"]
    ),
    (
      "cleanup-memories",
      "Clean up expired and stale memories from long-term storage",
      """
      When asked to clean up memories or running periodic maintenance:
      1. Use ListMemories with includeExpired=true to find expired memories
      2. Use ForgetMemory to delete each expired memory
      3. Use ListMemories to find unconfirmed memories (source: "inferred")
      4. For old unconfirmed memories, evaluate if they're still relevant
      5. Delete memories that are clearly outdated, redundant, or incorrect
      6. Use RecallMemory to check for conflicting memories on the same topic
      7. Resolve conflicts by keeping the most recent confirmed version
      8. Report what was cleaned up: expired count, stale count, conflicts resolved
      """,
      ["memory", "maintenance", "cleanup"]
    ),
    (
      "monitor-url",
      "Set up monitoring for a URL — check periodically for changes and report when something changes",
      """
      When the user wants to monitor a URL for changes (price drops, package tracking, status pages, etc.):
      1. Use FetchUrl to get the current content of the URL
      2. Use SaveMemory to store the current state with:
         - category: "monitor"
         - tags: descriptive (e.g. "package,tracking" or "price,product-name")
         - source: "system"
         - content: a concise summary of the current state (not the full page)
      3. Use CreateSchedule to set up periodic checking:
         - name: descriptive (e.g. "monitor-package-12345")
         - cron: choose frequency based on context (hourly for tracking, daily for prices)
         - prompt: "Use FetchUrl to check [URL]. Use RecallMemory with tags '[tags]' to get the previous state. Compare them. If anything meaningful changed, report the change and send a notification with SendNotification. Update the memory with the new state using UpdateMemory."
      4. Confirm to the user: what's being monitored, how often, what to look for
      """,
      ["monitoring", "automation", "fetch"]
    ),
    (
      "manage-list",
      "Manage markdown checklist lists stored in memory (shopping lists, packing lists, todo lists, etc.)",
      """
      Lists are stored as markdown checklists in memory with category "list".
      Each list is a single memory entry with the list name as a tag.

      To ADD an item to a list:
      1. Use RecallMemory with query "[list name] list" and category "list"
      2. If found: use UpdateMemory with the memory ID and new content (add "- [ ] item")
      3. If not found: create a new list with SaveMemory:
         - content: "# [List name]\n- [ ] item"
         - category: "list"
         - tags: the list name (e.g. "shopping", "packing")
         - source: "user"

      To SHOW a list:
      1. Use RecallMemory with the list name and category "list"
      2. Present the markdown to the user

      To CHECK OFF an item:
      1. Recall the list to get the ID and content
      2. Use UpdateMemory with the ID, changing "- [ ] item" to "- [x] item"

      To REMOVE an item:
      1. Recall the list, use UpdateMemory with the line removed

      To CLEAR completed items:
      1. Recall the list, use UpdateMemory with all "- [x]" lines removed

      To DELETE an entire list:
      1. Use ForgetMemory to remove it

      To LIST all lists:
      1. Use ListMemories with category "list"
      """,
      ["list", "checklist", "todo", "shopping"]
    ),
    (
      "manage-journal",
      "Manage a personal journal/notes stored in memory",
      """
      Journal entries are stored as memories with category "journal".
      Each entry is tagged with the date (e.g. "2026-04-03").

      To ADD a journal entry:
      1. Use SaveMemory with:
         - content: the journal entry text, prefixed with the date (e.g. "2026-04-03: Had a productive day...")
         - category: "journal"
         - tags: the date (e.g. "2026-04-03")
         - source: "user"

      To RECALL recent entries:
      1. Use ListMemories with category "journal" to browse recent entries
      2. Or use RecallMemory with a query to find entries about a specific topic

      To RECALL entries from a specific date:
      1. Use RecallMemory with the date and category "journal"

      To SUMMARIZE a period:
      1. Use ListMemories with category "journal" to get all entries
      2. Summarize the entries for the requested period

      To DELETE an entry:
      1. Find the entry via RecallMemory or ListMemories
      2. Use ForgetMemory to remove it

      When the user shares something personal, reflective, or diary-like, suggest saving it as a journal entry.
      """,
      ["journal", "notes", "diary"]
    ),
    (
      "use-displays",
      "Push content to user-owned web displays (e.g. wall-mounted iPads, kitchen tablets) registered with ruutu.",
      """
      Displays are physical screens (often an old iPad or wall-mounted tablet) you can push content to.
      Workflow:
      1. List displays with DisplayList. Each has an identifier (e.g. 'kitchen'), a tier (modern/legacy, auto-detected), and an online/offline status.
      2. List available content shapes with DisplayListTemplates. Each template has a name, description, and JSON Schema for its data.
      3. Push the current scene with DisplayShow(identifier, template, dataJson). The data must match the template's schema.
      4. Push transient cards with DisplayOverlay(identifier, template, dataJson). Overlays stack LIFO; the user must tap to dismiss. The 'notification' template is the common choice.
      5. Reset to idle with DisplayClear(identifier).
      Composite scenes: layout templates split_horizontal, split_vertical, and stack accept nested {template, data} blocks; the renderer composes them automatically.
      Authoring new templates: DisplayCreateTemplate requires both modern_html AND legacy_html variants. Legacy tier targets iOS Safari 9 (no flex/grid, no CSS variables, no WebP, no @import/@font-face — use tables, floats, system fonts). Modern tier is permissive. The server lints both before saving; iterate until the linter passes.
      Tap-back: when a user taps a checkbox or dismisses an overlay, a tap event is recorded. Use DisplayGetEvents(identifier, sinceUtc) to pull them when relevant (e.g. during an in-progress routine to track progress). v1 does NOT auto-trigger sessions on taps — you must query.
      """,
      ["displays", "ruutu", "ui", "iPad", "templates"]
    ),
  ];

  public async Task SeedAsync(CancellationToken ct = default)
  {
    foreach (var (name, description, instructions, tags) in StandardSkills)
    {
      var embeddingText = $"{description} {instructions}";
      var embedding = await embeddings.GenerateEmbeddingAsync(embeddingText);
      await repository.UpsertAsync(name, description, instructions.Trim(), tags, embedding, ct);
    }
  }
}
