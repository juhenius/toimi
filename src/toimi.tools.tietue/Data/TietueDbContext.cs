using Microsoft.EntityFrameworkCore;

namespace toimi.tools.tietue.Data;

public class TietueDbContext(DbContextOptions<TietueDbContext> options) : DbContext(options)
{
  public DbSet<Entity> Entities => Set<Entity>();
  public DbSet<TypeDefinition> TypeDefinitions => Set<TypeDefinition>();
  public DbSet<Trigger> Triggers => Set<Trigger>();
  public DbSet<EntityEvent> EntityEvents => Set<EntityEvent>();
  public DbSet<UniqueKey> UniqueKeys => Set<UniqueKey>();
  public DbSet<IndexOutbox> IndexOutbox => Set<IndexOutbox>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(TietueDbContext).Assembly);
  }
}
