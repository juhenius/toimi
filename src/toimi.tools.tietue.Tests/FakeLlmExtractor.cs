using toimi.tools.tietue.Scripts;

namespace toimi.tools.tietue.Tests;

public class FakeLlmExtractor : ILlmExtractor
{
  public List<(string Prompt, string Text, string? SchemaJson)> Calls { get; } = [];
  public string? NextResult { get; set; } = /*lang=json,strict*/ """{"ok":true}""";
  public Exception? NextException { get; set; }

  public Task<string?> ExtractAsync(string prompt, string text, string? schemaJson, CancellationToken ct = default)
  {
    if (NextException is not null)
    {
      throw NextException;
    }

    Calls.Add((prompt, text, schemaJson));
    return Task.FromResult(NextResult);
  }
}
