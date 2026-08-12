namespace Toimi.Notifications;

/// <summary>
/// Push-notification seam. NtfyClient is the production implementation;
/// tietue's NotifyHandler depends on this interface so tests can capture
/// sends (FakeNotifier) without HTTP.
/// </summary>
public interface INotifier
{
  Task SendAsync(string message, string? title = null, string priority = "default", string? tags = null, CancellationToken ct = default);
}
