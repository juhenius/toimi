namespace toimi.tools.tietue.Handlers;

public interface INativeHandler
{
  string Kind { get; }

  Task<HandlerResult> HandleAsync(HandlerContext ctx, CancellationToken ct = default);
}
