using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class UpdateEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Update an entity's data and/or tags. 'data' (if provided) replaces the entity's fields and is re-validated against the type schema.")]
  public async Task<string> Update(
      [Description("The entity id (GUID)")] string id,
      [Description("Optional new JSON object for the entity's fields")] string? data = null,
      [Description("Optional comma-separated tags (replaces existing)")] string? tags = null)
  {
    if (!Guid.TryParse(id, out var guid))
    {
      return "Invalid id format. Expected a GUID.";
    }

    JsonNode? node = null;
    if (data is not null)
    {
      try
      {
        node = JsonNode.Parse(data);
      }
      catch (JsonException ex)
      {
        return $"Invalid data JSON: {ex.Message}";
      }
    }

    try
    {
      var e = await repository.UpdateAsync(guid, node, tags is null ? null : ToolHelpers.ParseTags(tags));
      return e is null ? $"Entity '{id}' not found." : ToolHelpers.Render(e);
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
