using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace toimi.tools.tietue.Data;

public class TietueDbContextFactory : IDesignTimeDbContextFactory<TietueDbContext>
{
  public TietueDbContext CreateDbContext(string[] args)
  {
    var optionsBuilder = new DbContextOptionsBuilder<TietueDbContext>();
    optionsBuilder.UseNpgsql("Host=localhost;Database=tietue")
      .UseSnakeCaseNamingConvention();

    return new TietueDbContext(optionsBuilder.Options);
  }
}
