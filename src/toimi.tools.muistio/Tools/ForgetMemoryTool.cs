using System.ComponentModel;
using ModelContextProtocol.Server;
using toimi.tools.muistio.Memory;

namespace toimi.tools.muistio.Tools;

[McpServerToolType]
public class ForgetMemoryTool(MemoryRepository repository)
{
    [McpServerTool, Description("Delete a specific memory by ID. Use this when information is no longer relevant or was saved incorrectly.")]
    public async Task<string> ForgetMemory(
        [Description("Memory ID (UUID)")] string id)
    {
        if (!Guid.TryParse(id, out var memoryId))
            return "Invalid memory ID format. Expected a UUID.";

        var deleted = await repository.DeleteAsync(memoryId);
        return deleted ? "Memory deleted." : "Memory not found.";
    }
}
