using toimi.tools.tietue.Validation;

namespace toimi.tools.tietue.Handlers;

public interface INativeHandler
{
  string Kind { get; }

  Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default);

  /// <summary>
  /// Whether HandleAsync could do useful work with this trigger config. Called only by
  /// trigger-WRITING paths (set_trigger, update_trigger, define_type) — never by the
  /// scheduler or run_trigger, which fire whatever exists. Default: any config is fine.
  /// </summary>
  ValidationResult ValidateConfig(string? configJson)
  {
    return ValidationResult.Valid();
  }
}
