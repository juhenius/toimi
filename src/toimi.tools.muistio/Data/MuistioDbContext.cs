using Microsoft.EntityFrameworkCore;

namespace toimi.tools.muistio.Data;

public class MuistioDbContext(DbContextOptions<MuistioDbContext> options) : DbContext(options)
{
  public DbSet<Memory> Memories => Set<Memory>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(MuistioDbContext).Assembly);
  }
}
