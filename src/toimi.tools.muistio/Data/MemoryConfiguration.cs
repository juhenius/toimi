using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.muistio.Data;

public class MemoryConfiguration : IEntityTypeConfiguration<Memory>
{
  public void Configure(EntityTypeBuilder<Memory> builder)
  {
    builder.HasKey(m => m.Id);

    builder.Property(m => m.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(m => m.Content)
      .IsRequired();

    builder.Property(m => m.Source)
      .IsRequired()
      .HasDefaultValue("user");

    builder.Property(m => m.Confirmed)
      .HasDefaultValue(true);

    builder.Property(m => m.Tags)
      .HasColumnType("text[]");

    builder.Property(m => m.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(m => m.UpdatedAt)
      .HasDefaultValueSql("now()");

    builder.HasIndex(m => m.Category);

    builder.HasIndex(m => m.ExpiresAt);
  }
}
