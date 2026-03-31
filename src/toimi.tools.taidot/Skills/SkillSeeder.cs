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
/// Available tool servers and their tools (as of 2026-03-24):
/// - muistio: SaveMemory, RecallMemory, ForgetMemory, ListMemories
/// - muistutin: CreateReminder, ListReminders, CompleteReminder, DeleteReminder
/// - taidot: SaveSkill, GetSkill, FindSkill, ListSkills, DeleteSkill
/// - ajastin: CreateSchedule, ListSchedules, DeleteSchedule, EnableSchedule, DisableSchedule
/// - koti: GetEntityState, ListEntities (with area filter), CallService, GetHistory (Home Assistant REST API)
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
      """,
      ["reminder", "recurring"]
    ),
    (
      "schedule-recurring-task",
      "Create a scheduled agent task that runs on a cron schedule",
      """
      When the user wants something to run automatically on a schedule:
      1. Determine the cron expression from the user's description:
         - "every morning at 7" → 0 7 * * *
         - "every hour" → 0 * * * *
         - "every Monday at 9" → 0 9 * * 1
         - "every day at midnight" → 0 0 * * *
      2. Note: cron times are in UTC. Convert from user's timezone (Europe/Helsinki = UTC+2/+3)
      3. Write a clear, self-contained prompt that the agent will execute
      4. The prompt should be specific — the agent running it has no conversation context
      5. Use CreateSchedule with the name, cron expression, and prompt
      6. Confirm the schedule to the user, showing next run time in their timezone
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
