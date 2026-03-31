using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.ajastin.Data;

public class ScheduleRunConfiguration : IEntityTypeConfiguration<ScheduleRun>
{
  public void Configure(EntityTypeBuilder<ScheduleRun> builder)
  {
    builder.HasKey(r => r.Id);

    builder.Property(r => r.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(r => r.StartedAt)
      .IsRequired();

    builder.HasIndex(r => r.ScheduleId);
  }
}
