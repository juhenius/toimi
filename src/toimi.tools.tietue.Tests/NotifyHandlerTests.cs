using System.Text.Json;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class NotifyHandlerTests
{
  private static Entity Reminder(string title, string desc)
  {
    return new()
    {
      Id = Guid.NewGuid(),
      Type = "reminder",
      Data = JsonDocument.Parse($$"""{"title":"{{title}}","description":"{{desc}}"}"""),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
  }

  [Fact]
  public async Task Sends_rendered_notification()
  {
    var notifier = new FakeNotifier();
    var handler = new NotifyHandler(notifier);
    var config = /*lang=json,strict*/ """{"titleTemplate":"{title}","messageTemplate":"{description}","priority":"high","tags":"bell"}""";

    var result = await handler.HandleAsync(new HandlerContext(Reminder("Call mom", "use the new number"), config, DateTimeOffset.UtcNow));
    var (Message, Title, Priority, _) = Assert.Single(notifier.Sent);
    Assert.Equal("Call mom", Title);
    Assert.Equal("use the new number", Message);
    Assert.Equal("high", Priority);
    Assert.Equal("sent", result.Status);
  }

  [Fact]
  public async Task Falls_back_to_title_when_message_template_absent()
  {
    var notifier = new FakeNotifier();
    var handler = new NotifyHandler(notifier);

    await handler.HandleAsync(new HandlerContext(Reminder("Standup", ""), /*lang=json,strict*/ """{"titleTemplate":"{title}"}""", DateTimeOffset.UtcNow));

    Assert.Equal("Standup", notifier.Sent.Single().Message);
  }
}
