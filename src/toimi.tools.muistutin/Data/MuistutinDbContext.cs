using Microsoft.EntityFrameworkCore;

namespace toimi.tools.muistutin.Data;

public class MuistutinDbContext(DbContextOptions<MuistutinDbContext> options) : DbContext(options)
{
  public DbSet<Reminder> Reminders => Set<Reminder>();
  public DbSet<CompletedOccurrence> CompletedOccurrences => Set<CompletedOccurrence>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(MuistutinDbContext).Assembly);
  }
}
