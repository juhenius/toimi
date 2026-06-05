using toimi.tools.ruutu.Data.Repositories;

namespace toimi.tools.ruutu.Data;

public class TemplateSeeder(TemplateRepository repo, ILogger<TemplateSeeder> logger)
{
  public async Task SeedAsync(CancellationToken ct = default)
  {
    foreach (var t in SeedTemplates.All)
    {
      await repo.UpsertSeededAsync(t.Name, t.Description, t.SchemaJson, t.ModernHtml, t.LegacyHtml, ct);
#pragma warning disable CA1873
      logger.LogInformation("Seeded template '{Name}'", t.Name);
#pragma warning restore CA1873
    }
  }
}
