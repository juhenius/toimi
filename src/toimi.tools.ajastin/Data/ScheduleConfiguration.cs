using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.ajastin.Data;

public class ScheduleConfiguration : IEntityTypeConfiguration<Schedule>
{
  public void Configure(EntityTypeBuilder<Schedule> builder)
  {
    builder.HasKey(s => s.Id);

    builder.Property(s => s.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(s => s.Name)
      .IsRequired();

    builder.HasIndex(s => s.Name)
      .IsUnique();

    builder.Property(s => s.CronExpression)
      .IsRequired();

    builder.Property(s => s.Prompt)
      .IsRequired();

    builder.Property(s => s.Enabled)
      .HasDefaultValue(true);

    builder.Property(s => s.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.HasMany(s => s.Runs)
      .WithOne(r => r.Schedule)
      .HasForeignKey(r => r.ScheduleId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
