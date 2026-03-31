using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.muistio.Memory;

namespace toimi.tools.muistio.Tools;

[McpServerToolType]
public class RecallMemoryTool(EmbeddingService embeddings, MemoryRepository repository)
{
    [McpServerTool, Description("Search long-term memory by semantic similarity. Use this to recall facts, preferences, or context from previous conversations.")]
    public async Task<string> RecallMemory(
        [Description("Natural language query describing what you want to recall")] string query,
        [Description("Optional category filter (e.g. 'preference', 'fact')")] string? category = null,
        [Description("Optional tag filter (comma-separated, all must match)")] string? tags = null,
        [Description("Maximum number of results (default 5)")] int limit = 5)
    {
        if (limit is < 1 or > 100)
            return "Limit must be between 1 and 100.";

        var tagArray = string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var queryEmbedding = await embeddings.GenerateEmbeddingAsync(query);
        var results = await repository.RecallAsync(queryEmbedding, limit, category, tagArray);

        return results.Count == 0
            ? "No matching memories found."
            : JsonSerializer.Serialize(results.Select(m => new
            {
                m.Id, m.Content, m.Category, m.Tags, m.Score,
                m.Source, m.Confirmed,
                CreatedAt = m.CreatedAt.ToString("o")
            }));
    }
}
