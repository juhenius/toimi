using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace toimi.tools.muistutin.Data;

public class ReminderConfiguration : IEntityTypeConfiguration<Reminder>
{
  public void Configure(EntityTypeBuilder<Reminder> builder)
  {
    builder.HasKey(r => r.Id);

    builder.Property(r => r.Id)
      .HasDefaultValueSql("gen_random_uuid()");

    builder.Property(r => r.Title)
      .IsRequired();

    builder.Property(r => r.DateTimeUtc)
      .IsRequired();

    builder.Property(r => r.TimeZone)
      .IsRequired();

    builder.Property(r => r.IsCompleted)
      .HasDefaultValue(false);

    builder.Property(r => r.CreatedAt)
      .HasDefaultValueSql("now()");

    builder.Property(r => r.UpdatedAt)
      .HasDefaultValueSql("now()");

    builder.HasIndex(r => new { r.DateTimeUtc, r.DisplayEndUtc })
      .HasDatabaseName("idx_reminders_active_range")
      .HasFilter("NOT is_completed");

    builder.HasMany(r => r.CompletedOccurrences)
      .WithOne(co => co.Reminder)
      .HasForeignKey(co => co.ReminderId)
      .OnDelete(DeleteBehavior.Cascade);

    builder.HasMany(r => r.NotifiedOccurrences)
      .WithOne(n => n.Reminder)
      .HasForeignKey(n => n.ReminderId)
      .OnDelete(DeleteBehavior.Cascade);
  }
}
