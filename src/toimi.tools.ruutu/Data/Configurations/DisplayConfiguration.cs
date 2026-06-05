using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using toimi.tools.ruutu.Data.Entities;

namespace toimi.tools.ruutu.Data.Configurations;

public class DisplayConfiguration : IEntityTypeConfiguration<Display>
{
  public void Configure(EntityTypeBuilder<Display> builder)
  {
    builder.HasKey(d => d.Id);

    builder.HasIndex(d => d.Identifier).IsUnique();

    builder.Property(d => d.Identifier).IsRequired();

    builder.Property(d => d.CurrentData).HasColumnType("jsonb");
    builder.Property(d => d.OverlayStack).HasColumnType("jsonb").HasDefaultValue("[]");
    builder.Property(d => d.IdleData).HasColumnType("jsonb");

    builder.Property(d => d.CreatedAt).HasDefaultValueSql("now()");

    builder.HasMany(d => d.Events)
      .WithOne(e => e.Display)
      .HasForeignKey(e => e.DisplayId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
