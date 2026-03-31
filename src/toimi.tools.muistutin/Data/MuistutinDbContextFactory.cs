using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace toimi.tools.muistutin.Data;

public class MuistutinDbContextFactory : IDesignTimeDbContextFactory<MuistutinDbContext>
{
  public MuistutinDbContext CreateDbContext(string[] args)
  {
    var optionsBuilder = new DbContextOptionsBuilder<MuistutinDbContext>();
    optionsBuilder.UseNpgsql("Host=localhost;Database=muistutin")
      .UseSnakeCaseNamingConvention();

    return new MuistutinDbContext(optionsBuilder.Options);
  }
}
