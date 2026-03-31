using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Tools;

[McpServerToolType]
public class GetSkillTool(SkillRepository repository)
{
    [McpServerTool, Description("Get a skill by its exact name. Returns the full instructions for executing the skill.")]
    public async Task<string> GetSkill(
        [Description("The exact name of the skill to retrieve")] string name)
    {
        var skill = await repository.GetByNameAsync(name);

        if (skill is null)
            return "Skill not found.";

        return JsonSerializer.Serialize(new
        {
            skill.Id,
            skill.Name,
            skill.Description,
            skill.Instructions,
            skill.Tags,
            skill.CreatedAt,
        });
    }
}
