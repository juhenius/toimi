using System.Text.Json;
using Cronos;
using Microsoft.Extensions.AI;
using Toimi.Core;
using Toimi.Core.Configuration;
using toimi.tools.ajastin.Data;

namespace toimi.tools.ajastin.Worker;

public partial class ScheduleWorker(
    IServiceScopeFactory scopeFactory,
    ToimiConfiguration toimiConfig,
    ILogger<ScheduleWorker> logger) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken stoppingToken)
  {
    LogWorkerStarted(logger);

    while (!stoppingToken.IsCancellationRequested)
    {
      try
      {
        await CheckAndRunDueSchedules(stoppingToken);
      }
      catch (Exception ex)
      {
        LogWorkerLoopError(logger, ex);
      }

      await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
    }
  }

  private async Task CheckAndRunDueSchedules(CancellationToken ct)
  {
    using var scope = scopeFactory.CreateScope();
    var repository = scope.ServiceProvider.GetRequiredService<ScheduleRepository>();
    var schedules = await repository.GetEnabledAsync();
    var now = DateTimeOffset.UtcNow;

    foreach (var schedule in schedules)
    {
      if (ct.IsCancellationRequested)
      {
        break;
      }

      if (!IsDue(schedule, now))
      {
        continue;
      }

      LogRunningSchedule(logger, schedule.Name);
      await RunSchedule(schedule, repository, ct);
    }
  }

  private static bool IsDue(Schedule schedule, DateTimeOffset now)
  {
    // One-time schedule: run at specific time
    if (schedule.RunAt.HasValue)
    {
      return schedule.LastRunAt is null && schedule.RunAt.Value <= now;
    }

    // Cron schedule: check next occurrence
    if (string.IsNullOrEmpty(schedule.CronExpression))
    {
      return false;
    }

    var cron = CronExpression.Parse(schedule.CronExpression);
    var lastRun = schedule.LastRunAt?.UtcDateTime ?? schedule.CreatedAt.UtcDateTime;
    var nextOccurrence = cron.GetNextOccurrence(lastRun, inclusive: false);
    return nextOccurrence.HasValue && nextOccurrence.Value <= now.UtcDateTime;
  }

  private async Task RunSchedule(Schedule schedule, ScheduleRepository repository, CancellationToken ct)
  {
    var run = await repository.AddRunAsync(new ScheduleRun
    {
      ScheduleId = schedule.Id,
      StartedAt = DateTimeOffset.UtcNow,
    });

    try
    {
      var (response, toolCalls) = await ExecutePrompt(schedule.Prompt, ct);

      run.CompletedAt = DateTimeOffset.UtcNow;
      run.Response = response;
      run.ToolCallsJson = toolCalls.Count > 0 ? JsonSerializer.Serialize(toolCalls) : null;
      run.Success = true;

      schedule.LastRunAt = run.StartedAt;
      schedule.UpdatedAt = DateTimeOffset.UtcNow;
      if (schedule.RunAt.HasValue)
      {
        schedule.Enabled = false; // One-time schedule: disable after running
      }

      await repository.UpdateAsync(schedule);
    }
    catch (Exception ex)
    {
      run.CompletedAt = DateTimeOffset.UtcNow;
      run.Error = ex.Message;
      run.Success = false;
      LogScheduleFailed(logger, schedule.Name, ex);
    }

    await repository.UpdateRunAsync(run);
  }

  private async Task<(string Response, List<object> ToolCalls)> ExecutePrompt(string prompt, CancellationToken ct)
  {
    await using var aggregator = new McpToolAggregator();
    await aggregator.ConnectAllAsync(toimiConfig.McpServers, ct);

    var tools = aggregator.GetAllTools();
    var skillSummary = await aggregator.CallToolAsync("list_skills", ct: ct);
    var (client, notifier) = ToimiClientFactory.Create(toimiConfig);
    var options = ToimiClientFactory.CreateRequestOptions(tools);
    var messages = ToimiClientFactory.CreateInitialMessages(skillSummary);

    messages.Add(new(ChatRole.User, prompt));

    // Update current time and compact context if needed
    ToimiClientFactory.RefreshDynamicContext(messages);
    await ContextManager.CompactIfNeeded(messages, client, ct);

    // Use non-streaming GetResponseAsync for headless execution
    var response = await client.GetResponseAsync(messages, options, ct);
    var responseText = response.Text ?? "";

    // Collect tool call events
    var toolCalls = new List<object>();
    while (notifier.TryDequeueEvent(out var evt))
    {
      toolCalls.Add(evt!);
    }

    return (responseText, toolCalls);
  }

  [LoggerMessage(Level = LogLevel.Information, Message = "Schedule worker started.")]
  private static partial void LogWorkerStarted(ILogger logger);

  [LoggerMessage(Level = LogLevel.Error, Message = "Error in schedule worker loop.")]
  private static partial void LogWorkerLoopError(ILogger logger, Exception ex);

  [LoggerMessage(Level = LogLevel.Information, Message = "Running schedule '{Name}'")]
  private static partial void LogRunningSchedule(ILogger logger, string name);

  [LoggerMessage(Level = LogLevel.Error, Message = "Schedule '{Name}' failed.")]
  private static partial void LogScheduleFailed(ILogger logger, string name, Exception ex);
}
