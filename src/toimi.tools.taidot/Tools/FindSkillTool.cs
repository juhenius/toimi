using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Tools;

[McpServerToolType]
public class FindSkillTool(EmbeddingService embeddings, SkillRepository repository)
{
    [McpServerTool, Description("Search for skills by semantic similarity. Use this to find relevant skills when you're not sure of the exact name.")]
    public async Task<string> FindSkill(
        [Description("A natural-language description of the skill you're looking for")] string query,
        [Description("Optional tags to filter by (comma-separated)")] string? tags = null,
        [Description("Maximum number of results to return (1-100, default 5)")] int limit = 5)
    {
        limit = Math.Clamp(limit, 1, 100);

        var tagArray = string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var queryEmbedding = await embeddings.GenerateEmbeddingAsync(query);
        var results = await repository.SearchAsync(queryEmbedding, limit, tagArray);

        if (results.Count == 0)
            return "No matching skills found.";

        return JsonSerializer.Serialize(results.Select(s => new
        {
            s.Name,
            s.Description,
            s.Tags,
            s.Score,
        }));
    }
}
