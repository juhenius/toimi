using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Tools;

[McpServerToolType]
public class SaveSkillTool(EmbeddingService embeddings, SkillRepository repository)
{
    [McpServerTool, Description("Save a reusable skill (procedure/instructions) to the skill repository. If a skill with the same name exists, it will be updated.")]
    public async Task<string> SaveSkill(
        [Description("A short, unique name for the skill")] string name,
        [Description("A concise description of what the skill does")] string description,
        [Description("The full instructions for executing the skill")] string instructions,
        [Description("Optional tags for categorization (comma-separated, e.g. 'deploy,kubernetes')")] string? tags = null)
    {
        var tagArray = string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var embedding = await embeddings.GenerateEmbeddingAsync(description + " " + instructions);
        var id = await repository.UpsertAsync(name, description, instructions, tagArray, embedding);

        return JsonSerializer.Serialize(new { id, name, description });
    }
}
