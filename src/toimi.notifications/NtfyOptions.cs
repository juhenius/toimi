namespace Toimi.Notifications;

public class NtfyOptions
{
  public string BaseUrl { get; set; } = "http://localhost:8080";
  public string Topic { get; set; } = "toimi";
  public string? Username { get; set; }
  public string? Password { get; set; }
}
