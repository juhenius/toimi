using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Types;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class DefineTypeTool(TypeRepository repository)
{
  [McpServerTool, Description("Define or replace a data type by name. The schema is a JSON Schema (draft 2020-12) describing the shape of entities of this type. 'behaviors' is an optional JSON array of declarative behaviors, e.g. [{\"behavior\":\"SemanticIndex\",\"config\":{\"fields\":[\"content\"]}}]. Upserts by name.")]
  public async Task<string> DefineType(
      [Description("Unique type name, e.g. 'wishlist_item'")] string name,
      [Description("JSON Schema (draft 2020-12) for entities of this type")] string schema,
      [Description("Optional JSON array of behaviors (e.g. SemanticIndex)")] string? behaviors = null,
      [Description("Optional JSON array of default triggers stamped onto new entities")] string? defaultTriggers = null)
  {
    try
    {
      var t = await repository.DefineAsync(name, schema, behaviors, defaultTriggers);
      return JsonSerializer.Serialize(new { t.Name, defined = true });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
