using Toimi.Core.Data;
using Toimi.Web.Admin;
using Xunit;

namespace Toimi.Web.Tests;

public class UsageEndpointTests
{
  [Fact]
  public void Aggregates_by_day_and_prices_tokens()
  {
    var day1 = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);
    var messages = new List<ConversationMessage>
    {
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "a", CreatedAt = day1, PromptTokens = 1000, CompletionTokens = 500 },
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "b", CreatedAt = day1.AddHours(2), PromptTokens = 2000, CompletionTokens = 1000 },
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "c", CreatedAt = day1.AddDays(1), PromptTokens = 100, CompletionTokens = 50 },
    };

    var rows = UsageReport.Build(messages, inputPricePer1M: 2.50m, outputPricePer1M: 10.00m);

    Assert.Equal(2, rows.Count);
    var d1 = rows.Single(r => r.Date == new DateOnly(2026, 7, 1));
    Assert.Equal(3000, d1.PromptTokens);
    Assert.Equal(1500, d1.CompletionTokens);
    Assert.Equal((3000m / 1_000_000 * 2.50m) + (1500m / 1_000_000 * 10.00m), d1.CostUsd);
  }

  [Fact]
  public void Null_token_counts_sum_as_zero()
  {
    var day = new DateTimeOffset(2026, 7, 3, 8, 0, 0, TimeSpan.Zero);
    var messages = new List<ConversationMessage>
    {
      new() { ConversationId = Guid.NewGuid(), Role = "user", Content = "q", CreatedAt = day },
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "a", CreatedAt = day.AddMinutes(1), PromptTokens = 200, CompletionTokens = 40 },
    };

    var rows = UsageReport.Build(messages, inputPricePer1M: 2.50m, outputPricePer1M: 10.00m);

    var row = Assert.Single(rows);
    Assert.Equal(200, row.PromptTokens);
    Assert.Equal(40, row.CompletionTokens);
  }

  [Fact]
  public void Rows_are_ordered_by_date_ascending()
  {
    var messages = new List<ConversationMessage>
    {
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "later", CreatedAt = new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.Zero), PromptTokens = 1, CompletionTokens = 1 },
      new() { ConversationId = Guid.NewGuid(), Role = "assistant", Content = "earlier", CreatedAt = new DateTimeOffset(2026, 7, 2, 10, 0, 0, TimeSpan.Zero), PromptTokens = 1, CompletionTokens = 1 },
    };

    var rows = UsageReport.Build(messages, inputPricePer1M: 1m, outputPricePer1M: 1m);

    Assert.Equal(new DateOnly(2026, 7, 2), rows[0].Date);
    Assert.Equal(new DateOnly(2026, 7, 5), rows[1].Date);
  }
}
