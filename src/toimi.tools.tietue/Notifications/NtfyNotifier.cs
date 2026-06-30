using Toimi.Notifications;

namespace toimi.tools.tietue.Notifications;

public class NtfyNotifier(NtfyClient client) : INotifier
{
  public Task SendAsync(string message, string? title, string priority, string? tags, CancellationToken ct = default)
  {
    return client.SendAsync(message, title, priority, tags, ct);
  }
}
