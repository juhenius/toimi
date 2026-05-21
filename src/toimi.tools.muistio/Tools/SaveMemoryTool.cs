using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.muistio.Memory;

namespace toimi.tools.muistio.Tools;

[McpServerToolType]
public class SaveMemoryTool(EmbeddingService embeddings, MemoryRepository repository)
{
  [McpServerTool, Description("Save a fact or observation to long-term memory. Use this to remember important information about the user, their preferences, or anything worth recalling in future conversations.")]
  public async Task<string> SaveMemory(
      [Description("The fact or observation to remember")] string content,
      [Description("Optional category (e.g. 'preference', 'fact', 'context')")] string? category = null,
      [Description("Optional tags for filtering (comma-separated, e.g. 'weather,units')")] string? tags = null,
      [Description("Source: 'user' for user-stated facts, 'inferred' for AI-deduced facts (default 'user')")] string source = "user",
      [Description("Whether the user has confirmed this fact (default true)")] bool confirmed = true,
      [Description("Optional expiry datetime in ISO 8601 UTC (for temporary context)")] string? expiresAt = null)
  {
    var tagArray = string.IsNullOrWhiteSpace(tags)
        ? []
        : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    DateTimeOffset? parsedExpiry = null;
    if (expiresAt is not null && !DateTimeOffset.TryParse(expiresAt, out _))
    {
      return "Invalid expiresAt format. Use ISO 8601 (e.g. 2026-04-01T00:00:00Z).";
    }
    else if (expiresAt is not null)
    {
      parsedExpiry = DateTimeOffset.Parse(expiresAt, System.Globalization.CultureInfo.InvariantCulture);
    }

    var embedding = await embeddings.GenerateEmbeddingAsync(content);
    var entry = await repository.SaveAsync(content, embedding, category, tagArray,
        source: source, confirmed: confirmed, expiresAt: parsedExpiry);

    return JsonSerializer.Serialize(new
    {
      entry.Id,
      entry.Content,
      entry.Category,
      entry.Tags,
      entry.Source,
      entry.Confirmed,
      CreatedAt = entry.CreatedAt.ToString("o")
    });
  }
}
