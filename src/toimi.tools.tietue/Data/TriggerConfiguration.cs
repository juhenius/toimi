using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.tietue.Data;

public class TriggerConfiguration : IEntityTypeConfiguration<Trigger>
{
  public void Configure(EntityTypeBuilder<Trigger> builder)
  {
    builder.ToTable("triggers");
    builder.HasKey(t => t.Id);
    builder.Property(t => t.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(t => t.Schedule).HasColumnType("jsonb").IsRequired();
    builder.Property(t => t.HandlerKind).IsRequired();
    builder.Property(t => t.HandlerConfig).HasColumnType("jsonb");
    builder.Property(t => t.Source);
    builder.Property(t => t.Secret);
    builder.Property(t => t.Enabled).HasDefaultValue(true);
    builder.Property(t => t.CreatedAt).HasDefaultValueSql("now()");
    builder.Property(t => t.UpdatedAt).HasDefaultValueSql("now()");
    builder.HasIndex(t => new { t.Enabled, t.NextFireAt });
    builder.HasOne<Entity>()
      .WithMany()
      .HasForeignKey(t => t.EntityId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
