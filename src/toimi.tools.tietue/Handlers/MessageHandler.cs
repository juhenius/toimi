using System.Text.Json;
using toimi.tools.tietue.Agents;
using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

public class MessageHandler(IAgentRunner runner) : INativeHandler
{
  public string Kind => "message";

  public async Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default)
  {
    string? promptTemplate = null;
    if (ctx.ConfigJson is not null)
    {
      using var cfg = JsonDocument.Parse(ctx.ConfigJson);
      if (cfg.RootElement.TryGetProperty("promptTemplate", out var p) && p.ValueKind == JsonValueKind.String)
      {
        promptTemplate = p.GetString();
      }
    }

    var prompt = TemplateRenderer.Render(promptTemplate, ctx.Entity.Data);
    var run = await runner.RunAsync(ctx.Entity, prompt, ct);
    var result = JsonSerializer.Serialize(new
    {
      run.Response,
      run.Success,
      run.Error,
      promptTokens = run.PromptTokens,
      completionTokens = run.CompletionTokens,
    });
    return new HandlerResult(run.Success ? "ran" : "error", result);
  }

  public ValidationResult ValidateConfig(string? configJson)
  {
    const string Requirement = "message config requires 'promptTemplate' as a non-empty string — without it the agent runs with an empty prompt.";
    return ConfigValidation.RequireNonEmptyString(configJson, "promptTemplate", Requirement, requireNonWhitespace: true);
  }
}
