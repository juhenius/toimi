using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class IndexOutboxConfiguration : IEntityTypeConfiguration<IndexOutbox>
{
  public void Configure(EntityTypeBuilder<IndexOutbox> builder)
  {
    builder.ToTable("index_outbox");
    builder.HasKey(o => o.Id);
    builder.Property(o => o.Type).IsRequired();
    builder.Property(o => o.Op).IsRequired();
    builder.HasIndex(o => o.CreatedAt);
  }
}
