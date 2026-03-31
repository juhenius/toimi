using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace toimi.tools.ajastin.Data;

public class AjastinDbContextFactory : IDesignTimeDbContextFactory<AjastinDbContext>
{
  public AjastinDbContext CreateDbContext(string[] args)
  {
    var optionsBuilder = new DbContextOptionsBuilder<AjastinDbContext>();
    optionsBuilder.UseNpgsql("Host=localhost;Database=ajastin")
      .UseSnakeCaseNamingConvention();

    return new AjastinDbContext(optionsBuilder.Options);
  }
}
