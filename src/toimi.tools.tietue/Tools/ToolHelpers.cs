using System.Text.Json;
using toimi.tools.tietue.Data;

namespace toimi.tools.tietue.Tools;

internal static class ToolHelpers
{
  public static string[] ParseTags(string? tags)
  {
    return string.IsNullOrWhiteSpace(tags)
      ? []
      : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
  }

  public static string Render(Entity e)
  {
    return JsonSerializer.Serialize(new
    {
      id = e.Id.ToString(),
      type = e.Type,
      data = JsonDocument.Parse(e.Data.RootElement.GetRawText()),
      tags = e.Tags,
      createdAt = e.CreatedAt.ToString("o"),
      updatedAt = e.UpdatedAt.ToString("o"),
    });
  }
}
