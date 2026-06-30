using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class EntityEventConfiguration : IEntityTypeConfiguration<EntityEvent>
{
  public void Configure(EntityTypeBuilder<EntityEvent> builder)
  {
    builder.ToTable("entity_events");
    builder.HasKey(e => e.Id);
    builder.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(e => e.Kind).IsRequired();
    builder.Property(e => e.Status).IsRequired();
    builder.Property(e => e.Result).HasColumnType("jsonb");
    builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    builder.HasIndex(e => new { e.EntityId, e.OccurrenceUtc, e.Kind }).IsUnique();
    builder.HasOne<Entity>()
      .WithMany()
      .HasForeignKey(e => e.EntityId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
