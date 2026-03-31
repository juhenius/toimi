using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Tools;

[McpServerToolType]
public class ListSkillsTool(SkillRepository repository)
{
    [McpServerTool, Description("Browse all saved skills, optionally filtered by tag.")]
    public async Task<string> ListSkills(
        [Description("Optional tags to filter by (comma-separated)")] string? tags = null,
        [Description("Maximum number of results to return (1-100, default 20)")] int limit = 20)
    {
        limit = Math.Clamp(limit, 1, 100);

        var tagArray = string.IsNullOrWhiteSpace(tags)
            ? null
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = await repository.ListAsync(limit, tags: tagArray);

        if (results.Count == 0)
            return "No skills found.";

        return JsonSerializer.Serialize(results.Select(s => new
        {
            s.Name,
            s.Description,
            s.Tags,
            s.CreatedAt,
        }));
    }
}
