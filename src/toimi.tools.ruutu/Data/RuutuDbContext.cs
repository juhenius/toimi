using Microsoft.EntityFrameworkCore;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data;

public class RuutuDbContext(DbContextOptions<RuutuDbContext> options) : DbContext(options)
{
  public DbSet<Display> Displays => Set<Display>();
  public DbSet<Template> Templates => Set<Template>();
  public DbSet<DisplayEvent> DisplayEvents => Set<DisplayEvent>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(RuutuDbContext).Assembly);
  }
}
