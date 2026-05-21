using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.muistio.Memory;

namespace toimi.tools.muistio.Tools;

[McpServerToolType]
public class UpdateMemoryTool(EmbeddingService embeddings, MemoryRepository repository)
{
  [McpServerTool, Description("Update an existing memory by ID. Use this instead of ForgetMemory + SaveMemory to avoid data loss. Only the provided fields are updated — omitted fields are left unchanged.")]
  public async Task<string> UpdateMemory(
      [Description("Memory ID (UUID) to update")] string id,
      [Description("New content (replaces existing)")] string? content = null,
      [Description("New category")] string? category = null,
      [Description("New tags (comma-separated, replaces existing)")] string? tags = null,
      [Description("Whether the user has confirmed this fact")] bool? confirmed = null)
  {
    if (!Guid.TryParse(id, out var memoryId))
    {
      return "Invalid memory ID format. Expected a UUID.";
    }

    float[]? embedding = null;
    if (content is not null)
    {
      embedding = await embeddings.GenerateEmbeddingAsync(content);
    }

    var tagArray = tags?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var entry = await repository.UpdateAsync(memoryId, content, category, tagArray, confirmed, embedding: embedding);

    return entry is null
      ? "Memory not found."
      : JsonSerializer.Serialize(new
      {
        entry.Id,
        entry.Content,
        entry.Category,
        entry.Tags,
        entry.Source,
        entry.Confirmed,
        UpdatedAt = entry.UpdatedAt.ToString("o")
      });
  }
}
