namespace toimi.tools.ruutu.Rendering;

public record TemplateBody(string ModernHtml, string LegacyHtml);

public interface IRenderTemplateSource
{
  Task<TemplateBody?> GetAsync(string name, CancellationToken ct = default);
}
