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
  ];

  public async Task SeedAsync(CancellationToken ct = default)
  {
    foreach (var (name, schema, behaviors, defaultTriggers) in StandardTypes)
    {
      await repository.DefineAsync(name, schema, behaviors, defaultTriggers, ct: ct);
    }
  }
}
