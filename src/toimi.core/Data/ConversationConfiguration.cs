using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Toimi.Core.Data;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
  public void Configure(EntityTypeBuilder<Conversation> builder)
  {
    builder.HasKey(c => c.Id);

    builder.Property(c => c.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(c => c.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(c => c.LastMessageAt)
      .HasDefaultValueSql("now()");

    builder.HasMany(c => c.Messages)
      .WithOne(m => m.Conversation)
      .HasForeignKey(m => m.ConversationId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
