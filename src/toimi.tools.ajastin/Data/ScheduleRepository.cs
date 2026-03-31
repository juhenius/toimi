using Microsoft.EntityFrameworkCore;

namespace toimi.tools.ajastin.Data;

public class ScheduleRepository(AjastinDbContext dbContext)
{
  public async Task<Schedule> CreateAsync(Schedule schedule)
  {
    schedule.Id = Guid.NewGuid();
    schedule.CreatedAt = DateTimeOffset.UtcNow;

    dbContext.Schedules.Add(schedule);
    await dbContext.SaveChangesAsync();

    return schedule;
  }

  public async Task<Schedule?> GetByIdAsync(Guid id)
  {
    return await dbContext.Schedules.FindAsync(id);
  }

  public async Task<Schedule?> GetByNameAsync(string name)
  {
    return await dbContext.Schedules
      .FirstOrDefaultAsync(s => s.Name == name);
  }

  public async Task<IEnumerable<Schedule>> GetAllAsync()
  {
    return await dbContext.Schedules
      .OrderBy(s => s.Name)
      .ToListAsync();
  }

  public async Task<IEnumerable<Schedule>> GetEnabledAsync()
  {
    return await dbContext.Schedules
      .Where(s => s.Enabled)
      .ToListAsync();
  }

  public async Task<bool> DeleteAsync(Guid id)
  {
    var schedule = await dbContext.Schedules.FindAsync(id);
    if (schedule == null)
    {
      return false;
    }

    dbContext.Schedules.Remove(schedule);
    await dbContext.SaveChangesAsync();
    return true;
  }

  public async Task<Schedule> UpdateAsync(Schedule schedule)
  {
    dbContext.Schedules.Update(schedule);
    await dbContext.SaveChangesAsync();

    return schedule;
  }

  public async Task<ScheduleRun> AddRunAsync(ScheduleRun run)
  {
    dbContext.ScheduleRuns.Add(run);
    await dbContext.SaveChangesAsync();

    return run;
  }

  public async Task<ScheduleRun> UpdateRunAsync(ScheduleRun run)
  {
    dbContext.ScheduleRuns.Update(run);
    await dbContext.SaveChangesAsync();

    return run;
  }
}
