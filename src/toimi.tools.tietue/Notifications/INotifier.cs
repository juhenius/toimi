namespace toimi.tools.tietue.Notifications;

public interface INotifier
{
  Task SendAsync(string message, string? title, string priority, string? tags, CancellationToken ct = default);
}
