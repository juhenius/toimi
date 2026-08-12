using Toimi.Notifications;

namespace toimi.tools.tietue.Tests;

public class FakeNotifier : INotifier
{
  public List<(string Message, string? Title, string Priority, string? Tags)> Sent { get; } = [];

  public Task SendAsync(string message, string? title, string priority, string? tags, CancellationToken ct = default)
  {
    Sent.Add((message, title, priority, tags));
    return Task.CompletedTask;
  }
}
