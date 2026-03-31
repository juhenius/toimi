using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.muistutin.Data;

public class CompletedOccurrenceConfiguration : IEntityTypeConfiguration<CompletedOccurrence>
{
  public void Configure(EntityTypeBuilder<CompletedOccurrence> builder)
  {
    builder.HasKey(co => co.Id);

    builder.Property(co => co.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(co => co.OccurrenceUtc)
      .IsRequired();

    builder.Property(co => co.CompletedAt)
      .HasDefaultValueSql("now()");

    builder.HasIndex(co => new { co.ReminderId, co.OccurrenceUtc })
      .IsUnique();
  }
}
