using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Configurations;

public class DisplayEventConfiguration : IEntityTypeConfiguration<DisplayEvent>
{
  public void Configure(EntityTypeBuilder<DisplayEvent> builder)
  {
    builder.HasKey(e => e.Id);

    builder.Property(e => e.EventType).IsRequired();
    builder.Property(e => e.Value).HasColumnType("jsonb");
    builder.Property(e => e.CreatedAt).HasDefaultValueSql("now()");

    builder.HasIndex(e => new { e.DisplayId, e.CreatedAt })
      .HasDatabaseName("idx_display_events_display_created")
      .IsDescending(false, true);
  }
}
