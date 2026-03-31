using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Toimi.Core.Data;

public class ConversationMessageConfiguration : IEntityTypeConfiguration<ConversationMessage>
{
  public void Configure(EntityTypeBuilder<ConversationMessage> builder)
  {
    builder.HasKey(m => m.Id);

    builder.Property(m => m.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(m => m.Role)
      .IsRequired();

    builder.Property(m => m.Content)
      .IsRequired();

    builder.Property(m => m.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.HasIndex(m => m.ConversationId);
  }
}
