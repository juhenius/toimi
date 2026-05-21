using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.muistutin.Data;

public class NotifiedOccurrenceConfiguration : IEntityTypeConfiguration<NotifiedOccurrence>
{
  public void Configure(EntityTypeBuilder<NotifiedOccurrence> builder)
  {
    builder.HasKey(n => n.Id);
    builder.Property(n => n.Id).HasDefaultValueSql("gen_random_uuid()");
    builder.Property(n => n.OccurrenceUtc).IsRequired();
    builder.Property(n => n.NotifiedAt).HasDefaultValueSql("now()");

    builder.HasIndex(n => new { n.ReminderId, n.OccurrenceUtc }).IsUnique();
  }
}
