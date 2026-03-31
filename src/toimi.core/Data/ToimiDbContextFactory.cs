using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Toimi.Core.Data;

public class ToimiDbContextFactory : IDesignTimeDbContextFactory<ToimiDbContext>
{
  public ToimiDbContext CreateDbContext(string[] args)
  {
    var optionsBuilder = new DbContextOptionsBuilder<ToimiDbContext>();
    optionsBuilder.UseNpgsql("Host=localhost;Database=toimi")
      .UseSnakeCaseNamingConvention();

    return new ToimiDbContext(optionsBuilder.Options);
  }
}
