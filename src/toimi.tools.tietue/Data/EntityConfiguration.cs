using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class EntityConfiguration : IEntityTypeConfiguration<Entity>
{
  public void Configure(EntityTypeBuilder<Entity> builder)
  {
    builder.ToTable("entities");
    builder.HasKey(e => e.Id);

    builder.Property(e => e.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(e => e.Type)
      .IsRequired();

    builder.Property(e => e.Data)
      .HasColumnType("jsonb")
      .HasConversion(
        v => v.RootElement.GetRawText(),
        v => JsonDocument.Parse(v))
      .IsRequired();

    builder.Property(e => e.Tags)
      .HasColumnType("text[]");

    builder.Property(e => e.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(e => e.UpdatedAt)
      .HasDefaultValueSql("now()");

    builder.HasIndex(e => e.Type);
  }
}
