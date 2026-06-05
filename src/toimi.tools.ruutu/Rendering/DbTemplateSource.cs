using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Rendering;

public class DbTemplateSource(TemplateRepository templates) : IRenderTemplateSource
{
  public async Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default)
  {
    var t = await templates.GetAsync(name, ct);
    if (t is null) return null;
    return new TemplateBody(t.ModernHtml ?? "", t.LegacyHtml ?? "");
  }
}
