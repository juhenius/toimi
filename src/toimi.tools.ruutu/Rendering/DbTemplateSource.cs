using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Rendering;

public class DbTemplateSource(TemplateRepository templates) : IRenderTemplateSource
{
  public async Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default)
  {
    var t = await templates.GetAsync(name, ct);
    return t is null ? null : new TemplateBody(t.ModernHtml ?? "", t.LegacyHtml ?? "");
  }
}
