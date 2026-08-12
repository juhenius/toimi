using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using toimi.tools.tietue.Semantic;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Tools;

[McpServerToolType]
public class SearchEntitiesTool(SemanticSearch search)
{
  [McpServerTool, Description("Semantic search over entities of a type that has a SemanticIndex behavior. Returns the best-matching entities ranked by similarity. The type must be semantically indexed.")]
  public async Task<string> Search(
      [Description("The type name to search within (must have a SemanticIndex behavior)")] string type,
      [Description("Natural-language query")] string query,
      [Description("Max results (default 10)")] int limit = 10)
  {
    limit = Math.Clamp(limit, 1, 100);
    try
    {
      var results = await search.SearchAsync(type, query, limit);
      var items = results.Select(r => new JsonObject
      {
        ["id"] = r.Entity.Id.ToString(),
        ["type"] = r.Entity.Type,
        ["data"] = JsonNode.Parse(r.Entity.Data.RootElement.GetRawText()),
        ["tags"] = new JsonArray(r.Entity.Tags.Select(t => (JsonNode)t).ToArray()),
        ["score"] = r.Score,
      }).ToArray();

      return JsonSerializer.Serialize(new JsonObject { ["results"] = new JsonArray(items) });
    }
    catch (TietueValidationException ex)
    {
      return string.Join("; ", ex.Errors);
    }
  }
}
