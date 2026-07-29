using System.Net;
using System.Text.Json;
using Xunit;

namespace Toimi.Notifications.Tests;

public class NtfyClientTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
      return new HttpResponseMessage(HttpStatusCode.OK);
    }
  }

  private static async Task<int> SentPriority(string priority)
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message", priority: priority);

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    return doc.RootElement.GetProperty("priority").GetInt32();
  }

  [Theory]
  [InlineData("min", 1)]
  [InlineData("low", 2)]
  [InlineData("default", 3)]
  [InlineData("high", 4)]
  [InlineData("urgent", 5)]
  [InlineData("High", 4)]
  [InlineData("URGENT", 5)]
  [InlineData("Default", 3)]
  public async Task Priority_maps_case_insensitively(string priority, int expected)
  {
    // tietue's NotifyHandler passes user-authored behavior config straight through;
    // "High" or "URGENT" silently downgrading to normal means an urgent alert never
    // breaks through Do Not Disturb.
    Assert.Equal(expected, await SentPriority(priority));
  }

  [Theory]
  [InlineData("bogus")]
  [InlineData("")]
  public async Task Unknown_priority_falls_back_to_normal(string priority)
  {
    Assert.Equal(3, await SentPriority(priority));
  }
}
