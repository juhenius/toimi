using Microsoft.EntityFrameworkCore;

namespace Toimi.Core.Data;

public class ToimiDbContext(DbContextOptions<ToimiDbContext> options) : DbContext(options)
{
  public DbSet<Conversation> Conversations => Set<Conversation>();
  public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();

  protected override void OnModelCreating(ModelBuilder modelBuilder)
  {
    modelBuilder.ApplyConfigurationsFromAssembly(typeof(ToimiDbContext).Assembly);
  }
}
