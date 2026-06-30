using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class UniqueKeyConfiguration : IEntityTypeConfiguration<UniqueKey>
{
  public void Configure(EntityTypeBuilder<UniqueKey> builder)
  {
    builder.ToTable("unique_keys");
    builder.HasKey(k => k.Id);
    builder.Property(k => k.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(k => k.Type).IsRequired();
    builder.Property(k => k.Field).IsRequired();
    builder.Property(k => k.Value).IsRequired();
    builder.HasIndex(k => new { k.Type, k.Field, k.Value }).IsUnique();
    builder.HasIndex(k => k.EntityId);
    builder.HasOne<Entity>()
      .WithMany()
      .HasForeignKey(k => k.EntityId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
