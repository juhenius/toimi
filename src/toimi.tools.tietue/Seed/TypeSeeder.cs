using toimi.tools.tietue.Handlers;
using toimi.tools.tietue.Types;

namespace toimi.tools.tietue.Seed;

public class TypeSeeder(TypeRepository repository)
{
  private static readonly (string Name, string Schema, string? Behaviors, string? DefaultTriggers)[] StandardTypes =
  [
    (
      "memory",
      /*lang=json,strict*/
                           """
      {"type":"object","properties":{
        "content":{"type":"string","description":"the fact or observation to remember"},
        "category":{"type":"string","description":"optional category, e.g. preference/fact/context"},
        "source":{"type":"string","description":"user or inferred"},
        "confirmed":{"type":"boolean"},
        "expiresAt":{"type":"string","description":"optional ISO 8601 UTC time after which this memory is auto-deleted"}
      },"required":["content"]}
      """,
      /*lang=json,strict*/
                           """[{"behavior":"SemanticIndex","config":{"fields":["content"],"mode":"whole"}},{"behavior":"Expiry","config":{"field":"expiresAt"}}]""",
      null
    ),
    (
      "skill",
      /*lang=json,strict*/
                           """
      {"type":"object","properties":{
        "name":{"type":"string","description":"short unique skill name"},
        "description":{"type":"string","description":"what the skill does"},
        "instructions":{"type":"string","description":"full step-by-step instructions"}
      },"required":["name","description","instructions"]}
      """,
      /*lang=json,strict*/
                           """[{"behavior":"SemanticIndex","config":{"fields":["description","instructions"],"mode":"whole"}},{"behavior":"UniqueName","config":{"field":"name"}}]""",
      null
    ),
    (
      "reminder",
      /*lang=json,strict*/
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
      /*lang=json,strict*/
                           """[{"when":{"atField":"dueAt","rruleField":"rrule","tzField":"timezone"},"handler":{"kind":"notify","config":{"titleTemplate":"{title}","messageTemplate":"{description}","tags":"bell"}}}]"""
    ),
    (
      "schedule",
      /*lang=json,strict*/
                           """
      {"type":"object","properties":{
        "name":{"type":"string","description":"short schedule name"},
        "prompt":{"type":"string","description":"the instruction the agent runs each time"},
        "startAt":{"type":"string","description":"first run time, ISO 8601 UTC"},
        "rrule":{"type":"string","description":"optional RFC 5545 RRULE for recurrence (e.g. FREQ=DAILY)"}
      },"required":["name","prompt","startAt"]}
      """,
      null,
      /*lang=json,strict*/
                           """[{"when":{"atField":"startAt","rruleField":"rrule"},"handler":{"kind":"message","config":{"promptTemplate":"{prompt}"}}}]"""
    ),
    (
      ScriptHandler.JobTypeName,
      /*lang=json,strict*/
                           """
      {"type":"object","properties":{
        "name":{"type":"string","description":"short unique job name"},
        "description":{"type":"string","description":"what the job does"},
        "code":{"type":"string","description":"ES module source. Must default-export an async function(input) returning an effects object: {\"setField\":[{\"path\":\"field\",\"value\":...}],\"mcpCall\":[{\"tool\":\"tool_name\",\"args\":{...}}]}. input has data (this entity's fields), entityId, entityType, occurrence, params (call-time arguments: {} for scheduled runs, the caller's query/body for webhook-fired runs), and — with the llm grant — extract(prompt, text, schema) for LLM-parsing fetched content. fetch() works for hosts listed in allowedHosts. Besides the startAt/rrule schedule, a job can also be fired by HTTP call: set_trigger with {\"webhook\":{...}} and handler {\"kind\":\"script\",\"config\":{\"fromEntity\":true}} returns a capability URL."},
        "allowedHosts":{"type":"array","items":{"type":"string"},"description":"hostnames the script may fetch, e.g. api.open-meteo.com"},
        "grants":{"type":"array","items":{"type":"string"},"description":"capability grants: setField, llm, and mcp:<toolName> per MCP tool the effects may call (e.g. mcp:display_show, mcp:send_notification). WARNING: granting mcp:update or mcp:set_trigger lets the job rewrite its own code or schedule — grant these only deliberately."},
        "startAt":{"type":"string","description":"first scheduled run, ISO 8601 UTC. Omit for a webhook-only job (no time trigger is provisioned; add the call anchor with set_trigger). Editing startAt/rrule/tz after creation does NOT reschedule the existing trigger (copy-down happens at create only) — use update_trigger instead."},
        "rrule":{"type":"string","description":"optional RFC 5545 RRULE for recurrence (e.g. FREQ=MINUTELY;INTERVAL=30). Sub-daily rules (MINUTELY/HOURLY) must use plain INTERVAL form — BY-part filters combined with tz are not supported; use FREQ=DAILY with BYHOUR/BYMINUTE for wall-clock times"},
        "tz":{"type":"string","description":"IANA tz for recurrence, e.g. Europe/Helsinki"},
        "enabled":{"type":"boolean","description":"set false to pause the job"}
      },"required":["name","code"],"dependentRequired":{"rrule":["startAt"]}}
      """,
      /*lang=json,strict*/
                           """[{"behavior":"UniqueName","config":{"field":"name"}}]""",
      /*lang=json,strict*/
                           """[{"when":{"atField":"startAt","rruleField":"rrule","tzField":"tz"},"handler":{"kind":"script","config":{"fromEntity":true}}}]"""
    ),
  ];

  public async Task SeedAsync(CancellationToken ct = default)
  {
    foreach (var (name, schema, behaviors, defaultTriggers) in StandardTypes)
    {
      await repository.DefineAsync(name, schema, behaviors, defaultTriggers, ct: ct);
    }
  }
}
