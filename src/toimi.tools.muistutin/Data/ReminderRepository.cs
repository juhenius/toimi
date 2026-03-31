using Microsoft.EntityFrameworkCore;

namespace toimi.tools.muistutin.Data;

public class ReminderRepository(MuistutinDbContext dbContext)
{
  public async Task<Reminder> CreateAsync(Reminder reminder)
  {
    reminder.Id = Guid.NewGuid();
    reminder.CreatedAt = DateTimeOffset.UtcNow;

    dbContext.Reminders.Add(reminder);
    await dbContext.SaveChangesAsync();

    return reminder;
  }

  public async Task<Reminder?> GetByIdAsync(Guid id)
  {
    return await dbContext.Reminders.FindAsync(id);
  }

  public async Task<IEnumerable<Reminder>> GetByDateRangeAsync(DateTimeOffset from, DateTimeOffset to)
  {
    return await dbContext.Reminders
      .Where(r => !r.IsCompleted
        && r.DateTimeUtc <= to
        && (r.DisplayEndUtc >= from || r.DisplayEndUtc == null))
      .ToListAsync();
  }

  public async Task<IEnumerable<CompletedOccurrence>> GetCompletedOccurrencesAsync(Guid reminderId, DateTimeOffset from, DateTimeOffset to)
  {
    return await dbContext.CompletedOccurrences
      .Where(co => co.ReminderId == reminderId
        && co.OccurrenceUtc >= from
        && co.OccurrenceUtc <= to)
      .ToListAsync();
  }

  public async Task CompleteAsync(Guid id)
  {
    var reminder = await dbContext.Reminders.FindAsync(id);
    if (reminder != null)
    {
      reminder.IsCompleted = true;
      await dbContext.SaveChangesAsync();
    }
  }

  public async Task CompleteOccurrenceAsync(Guid reminderId, DateTimeOffset occurrenceUtc)
  {
    var exists = await dbContext.CompletedOccurrences
      .AnyAsync(co => co.ReminderId == reminderId && co.OccurrenceUtc == occurrenceUtc);

    if (!exists)
    {
      dbContext.CompletedOccurrences.Add(new CompletedOccurrence
      {
        ReminderId = reminderId,
        OccurrenceUtc = occurrenceUtc,
      });
      await dbContext.SaveChangesAsync();
    }
  }

  public async Task<bool> DeleteAsync(Guid id)
  {
    var reminder = await dbContext.Reminders.FindAsync(id);
    if (reminder == null)
    {
      return false;
    }

    dbContext.Reminders.Remove(reminder);
    await dbContext.SaveChangesAsync();
    return true;
  }
}
