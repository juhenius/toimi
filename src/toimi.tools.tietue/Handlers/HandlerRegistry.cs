namespace toimi.tools.tietue.Handlers;

public class HandlerRegistry(IEnumerable<INativeHandler> handlers)
{
  private readonly Dictionary<string, INativeHandler> _byKind = handlers.ToDictionary(h => h.Kind);

  public IReadOnlyCollection<string> Kinds => [.. _byKind.Keys];

  public INativeHandler? Resolve(string kind)
  {
    return _byKind.GetValueOrDefault(kind);
  }
}
