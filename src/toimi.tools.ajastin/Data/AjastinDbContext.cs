using Microsoft.EntityFrameworkCore;

namespace toimi.tools.ajastin.Data;

public class AjastinDbContext(DbContextOptions<AjastinDbContext> options) : DbContext(options)
{
  public DbSet<Schedule> Schedules => Set<Schedule>();
  public DbSet<ScheduleRun> ScheduleRuns => Set<ScheduleRun>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(AjastinDbContext).Assembly);
  }
}
