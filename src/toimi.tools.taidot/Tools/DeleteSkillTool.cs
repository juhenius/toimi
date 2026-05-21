using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.taidot.Skills;

namespace toimi.tools.taidot.Tools;

[McpServerToolType]
public class DeleteSkillTool(SkillRepository repository)
{
  [McpServerTool, Description("Delete a skill by name.")]
  public async Task<string> DeleteSkill(
      [Description("The exact name of the skill to delete")] string name)
  {
    var deleted = await repository.DeleteByNameAsync(name);

    return deleted
        ? $"Skill '{name}' deleted."
        : "Skill not found.";
  }
}
