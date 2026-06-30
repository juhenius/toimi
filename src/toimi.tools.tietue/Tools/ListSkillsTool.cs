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
