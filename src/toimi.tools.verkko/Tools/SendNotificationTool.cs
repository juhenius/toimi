using System.ComponentModel;
using ModelContextProtocol.Server;
using Toimi.Notifications;

namespace toimi.tools.verkko.Tools;

[McpServerToolType]
public class SendNotificationTool(NtfyClient ntfy)
{
  private static readonly string[] ValidPriorities = ["min", "low", "default", "high", "urgent"];

  [McpServerTool, Description("Send a push notification to the user via ntfy. Use this for alerts, reminders, monitoring notifications, and any time the user should be notified outside of the chat.")]
  public async Task<string> SendNotification(
    [Description("Notification message body")] string message,
    [Description("Optional notification title")] string? title = null,
    [Description("Priority: 'min', 'low', 'default', 'high', 'urgent' (default 'default')")] string priority = "default",
    [Description("Optional comma-separated emoji tags (e.g. 'package,delivered' or 'warning')")] string? tags = null)
  {
    if (!ValidPriorities.Contains(priority))
    {
      return $"Invalid priority. Use one of: {string.Join(", ", ValidPriorities)}";
    }

    try
    {
      await ntfy.SendAsync(message, title, priority, tags);
      return "Notification sent.";
    }
    catch (Exception ex)
    {
      return $"Failed to send notification: {ex.Message}";
    }
  }
}
