using System.Text.Json;
using toimi.tools.tietue.Data;
using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class MessageHandlerTests
{
  private static Entity Schedule(string prompt)
  {
    return new()
    {
      Id = Guid.NewGuid(),
      Type = "schedule",
      Data = JsonDocument.Parse($$"""{"name":"daily","prompt":"{{prompt}}"}"""),
      CreatedAt = DateTimeOffset.UtcNow,
      UpdatedAt = DateTimeOffset.UtcNow,
    };
  }

  [Fact]
  public async Task Renders_prompt_from_data_and_runs_agent()
  {
    var runner = new FakeAgentRunner();
    var handler = new MessageHandler(runner);
    var e = Schedule("Give me a morning briefing");

    var result = await handler.HandleAsync(new HandlerContext(e, /*lang=json,strict*/ """{"promptTemplate":"{prompt}"}""", DateTimeOffset.UtcNow));

    var run = Assert.Single(runner.Runs);
    Assert.Equal("Give me a morning briefing", run.Prompt);
    Assert.Same(e, run.Entity);
    Assert.Equal("ran", result.Status);
  }

  [Fact]
  public async Task Serializes_usage_into_result_json()
  {
    var runner = new FakeAgentRunner { Result = new(true, "done", null, null, PromptTokens: 1200, CompletionTokens: 340) };
    var handler = new MessageHandler(runner);

    var result = await handler.HandleAsync(new HandlerContext(Schedule("x"), /*lang=json,strict*/ """{"promptTemplate":"{prompt}"}""", DateTimeOffset.UtcNow));

    Assert.Contains("\"promptTokens\":1200", result.Result);
    Assert.Contains("\"completionTokens\":340", result.Result);
  }

  [Fact]
  public async Task Reports_error_status_when_run_fails()
  {
    var runner = new FakeAgentRunner { Result = new(false, "", null, "boom") };
    var handler = new MessageHandler(runner);

    var result = await handler.HandleAsync(new HandlerContext(Schedule("x"), /*lang=json,strict*/ """{"promptTemplate":"{prompt}"}""", DateTimeOffset.UtcNow));

    Assert.Equal("error", result.Status);
    Assert.Contains("boom", result.Result);
  }

  [Theory]
  [InlineData(null)]
  [InlineData("{}")]
  [InlineData(/*lang=json,strict*/ """{"promptTemplate":""}""")]
  [InlineData(/*lang=json,strict*/ """{"promptTempalte":"typo'd key"}""")]
  public void ValidateConfig_rejects_configs_that_run_an_empty_prompt(string? config)
  {
    var result = new MessageHandler(new FakeAgentRunner()).ValidateConfig(config);
    Assert.False(result.IsValid);
    Assert.Contains("promptTemplate", result.Errors[0]);
  }

  [Fact]
  public void ValidateConfig_accepts_a_prompt_template()
  {
    Assert.True(new MessageHandler(new FakeAgentRunner())
      .ValidateConfig(/*lang=json,strict*/ """{"promptTemplate":"{prompt}"}""").IsValid);
  }
}
