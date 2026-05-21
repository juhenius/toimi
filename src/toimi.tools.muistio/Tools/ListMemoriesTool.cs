using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.muistio.Memory;

namespace toimi.tools.muistio.Tools;

[McpServerToolType]
public class ListMemoriesTool(MemoryRepository repository)
{
  [McpServerTool, Description("Browse all saved memories, optionally filtered by category or tags. Use this to see what has been remembered.")]
  public async Task<string> ListMemories(
      [Description("Optional category filter")] string? category = null,
      [Description("Optional tag filter (comma-separated, all must match)")] string? tags = null,
      [Description("Maximum number of results (default 20)")] int limit = 20,
      [Description("Offset for pagination (default 0)")] int offset = 0)
  {
    if (limit is < 1 or > 100)
    {
      return "Limit must be between 1 and 100.";
    }

    if (offset < 0)
    {
      return "Offset must not be negative.";
    }

    var tagArray = string.IsNullOrWhiteSpace(tags)
        ? null
        : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var results = await repository.ListAsync(limit, offset, category, tagArray);

    return results.Count == 0
        ? "No memories found."
        : JsonSerializer.Serialize(results.Select(m => new
        {
          m.Id,
          m.Content,
          m.Category,
          m.Tags,
          m.Source,
          m.Confirmed,
          CreatedAt = m.CreatedAt.ToString("o")
        }));
  }
}
