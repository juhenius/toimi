using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace toimi.tools.muistio.Data;

public class MuistioDbContextFactory : IDesignTimeDbContextFactory<MuistioDbContext>
{
  public MuistioDbContext CreateDbContext(string[] args)
  {
    var optionsBuilder = new DbContextOptionsBuilder<MuistioDbContext>();
    optionsBuilder.UseNpgsql("Host=localhost;Database=muistio")
      .UseSnakeCaseNamingConvention();

    return new MuistioDbContext(optionsBuilder.Options);
  }
}
