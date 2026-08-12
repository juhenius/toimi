using toimi.tools.verkko.Tools;
using Toimi.Notifications;
using Xunit;

namespace toimi.tools.verkko.Tests;

public class SendNotificationToolTests
{
  private sealed class FailingHandler : HttpMessageHandler
  {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      throw new HttpRequestException("connection refused");
    }
  }

  [Fact]
  public async Task Send_failure_returns_the_error_string_instead_of_throwing()
  {
    var ntfy = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(new FailingHandler()));
    var tool = new SendNotificationTool(ntfy);

    var result = await tool.SendNotification("hello");

    Assert.StartsWith("Failed to send notification: ", result);
    Assert.Contains("connection refused", result);
  }
}
