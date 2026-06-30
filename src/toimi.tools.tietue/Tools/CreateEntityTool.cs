using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Entities;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class CreateEntityTool(EntityRepository repository)
{
  [McpServerTool, Description("Create an entity of a defined type. 'data' is a JSON object validated against the type's schema. Use list_types first to learn the schema.")]
  public async Task<string> Create(
      [Description("The type name (must be defined)")] string type,
      [Description("JSON object with the entity's fields")] string data,
      [Description("Optional comma-separated tags")] string? tags = null)
  {
    JsonNode? node;
    try
    {
      node = JsonNode.Parse(data);
    }
    catch (JsonException ex)
    {
      return $"Invalid data JSON: {ex.Message}";
    }

    try
    {
      var e = await repository.CreateAsync(type, node, ToolHelpers.ParseTags(tags));
      return ToolHelpers.Render(e);
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
