using Microsoft.EntityFrameworkCore;
using Toimi.Notifications;
using toimi.tools.muistutin.Data;
using toimi.tools.muistutin.Recurrence;

namespace toimi.tools.muistutin.Worker;

public partial class ReminderNotifier(
    IServiceScopeFactory scopeFactory,
    NtfyClient ntfy,
    ILogger<ReminderNotifier> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    logger.LogInformation("Reminder notifier started.");

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await CheckAndNotify(stoppingToken);
      }
      catch (Exception ex)
      {
        logger.LogError(ex, "Error in reminder notifier loop.");
      }

      await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
    }
  }

  private async Task CheckAndNotify(CancellationToken ct)
  {
    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MuistutinDbContext>();
    var now = DateTimeOffset.UtcNow;

    // Get active reminders that could be due
    var reminders = await db.Reminders
      .Include(r => r.CompletedOccurrences)
      .Include(r => r.NotifiedOccurrences)
      .Where(r => !r.IsCompleted && r.DateTimeUtc <= now)
      .ToListAsync(ct);

    foreach (var reminder in reminders)
    {
      if (ct.IsCancellationRequested)
      {
        break;
      }

      if (string.IsNullOrEmpty(reminder.RecurrenceRule))
      {
        // One-time reminder
        await NotifyOneTime(reminder, db, ct);
      }
      else
      {
        // Recurring reminder
        await NotifyRecurring(reminder, now, db, ct);
      }
    }
  }

  private async Task NotifyOneTime(Reminder reminder, MuistutinDbContext db, CancellationToken ct)
  {
    if (reminder.NotifiedAt is not null)
    {
      return;
    }

    await SendNotification(reminder);

    reminder.NotifiedAt = DateTimeOffset.UtcNow;
    await db.SaveChangesAsync(ct);

    LogNotifiedOneTime(logger, reminder.Title);
  }

  private async Task NotifyRecurring(Reminder reminder, DateTimeOffset now, MuistutinDbContext db, CancellationToken ct)
  {
    // Check occurrences in a window: from last check to now
    // Use a 5-minute window to catch any we might have missed
    var windowStart = now.AddMinutes(-5);

    var occurrences = RecurrenceExpander.ExpandOccurrences(
      reminder.DateTimeUtc, reminder.RecurrenceRule, windowStart, now);

    var notifiedSet = reminder.NotifiedOccurrences
      .Select(n => n.OccurrenceUtc)
      .ToHashSet();

    var completedSet = reminder.CompletedOccurrences
      .Select(c => c.OccurrenceUtc)
      .ToHashSet();

    foreach (var occurrence in occurrences)
    {
      if (notifiedSet.Contains(occurrence) || completedSet.Contains(occurrence))
      {
        continue;
      }

      await SendNotification(reminder);

      db.NotifiedOccurrences.Add(new NotifiedOccurrence
      {
        ReminderId = reminder.Id,
        OccurrenceUtc = occurrence,
      });

      LogNotifiedRecurring(logger, reminder.Title, occurrence);
    }

    await db.SaveChangesAsync(ct);
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Notified one-time reminder: {Title}")]
  private static partial void LogNotifiedOneTime(ILogger logger, string title);

  [LoggerMessage(Level = LogLevel.Information, Message = "Notified recurring reminder: {Title} at {Occurrence}")]
  private static partial void LogNotifiedRecurring(ILogger logger, string title, DateTimeOffset occurrence);

  private async Task SendNotification(Reminder reminder)
  {
    var message = reminder.Description ?? reminder.Title;
    try
    {
      await ntfy.SendAsync(message, title: reminder.Title, priority: "default", tags: "bell");
    }
    catch (Exception ex)
    {
      logger.LogError(ex, "Failed to send notification for reminder: {Title}", reminder.Title);
    }
  }
}
