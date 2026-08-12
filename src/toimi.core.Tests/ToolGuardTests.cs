using Microsoft.Extensions.Logging;
using Toimi.Core.Tools;
using Xunit;

namespace Toimi.Core.Tests;

public class ToolGuardTests
{
  private sealed class CapturingLogger : ILogger
  {
    public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
      return null;
    }

    public bool IsEnabled(LogLevel logLevel)
    {
      return true;
    }

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
      Entries.Add((logLevel, exception));
    }
  }

  [Fact]
  public async Task Success_passes_the_body_result_through()
  {
    var result = await ToolGuard.RunAsync(() => Task.FromResult("ok"));

    Assert.Equal("ok", result);
  }

  [Fact]
  public async Task Translated_exception_returns_the_pinned_message()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new HttpRequestException("boom"),
      translate: ex => ex is HttpRequestException http ? $"Request failed: {http.Message}" : null);

    Assert.Equal("Request failed: boom", result);
  }

  [Fact]
  public async Task Translator_declining_falls_through_to_the_backstop()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("nope"),
      translate: ex => ex is HttpRequestException ? "unreachable" : null);

    Assert.Equal("Error: nope", result);
  }

  [Fact]
  public async Task Backstop_without_a_translator_uses_the_default_Error_prefix()
  {
    var result = await ToolGuard.RunAsync(() => throw new InvalidOperationException("nope"));

    Assert.Equal("Error: nope", result);
  }

  [Fact]
  public async Task Backstop_uses_a_custom_prefix()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("smtp down"),
      errorPrefix: "Failed to send notification");

    Assert.Equal("Failed to send notification: smtp down", result);
  }

  [Fact]
  public async Task Backstop_logs_the_untranslated_exception()
  {
    var logger = new CapturingLogger();

    _ = await ToolGuard.RunAsync(() => throw new InvalidOperationException("nope"), logger: logger);

    var entry = Assert.Single(logger.Entries);
    Assert.Equal(LogLevel.Error, entry.Level);
    Assert.IsType<InvalidOperationException>(entry.Exception);
  }

  [Fact]
  public async Task Translated_exceptions_are_not_logged()
  {
    var logger = new CapturingLogger();

    _ = await ToolGuard.RunAsync(
      () => throw new TimeoutException(),
      translate: ex => ex is TimeoutException ? "The page is busy." : null,
      logger: logger);

    Assert.Empty(logger.Entries);
  }

  [Fact]
  public async Task Cancellation_is_stringified_like_any_other_failure()
  {
    // Matches ruutu's existing catch-all behavior: tools take a CancellationToken
    // and a cancelled call comes back as a string, never as a thrown OCE.
    var result = await ToolGuard.RunAsync(() => throw new OperationCanceledException());

    Assert.StartsWith("Error: ", result);
  }

  [Fact]
  public async Task A_throwing_translate_delegate_falls_through_to_the_backstop()
  {
    var result = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("nope"),
      translate: _ => throw new NotSupportedException("translate blew up"));

    Assert.Equal("Error: nope", result);
  }

  [Fact]
  public async Task A_throwing_translate_delegate_still_logs_the_original_exception()
  {
    var logger = new CapturingLogger();

    _ = await ToolGuard.RunAsync(
      () => throw new InvalidOperationException("nope"),
      translate: _ => throw new NotSupportedException("translate blew up"),
      logger: logger);

    Assert.Equal(2, logger.Entries.Count);
    Assert.IsType<InvalidOperationException>(logger.Entries[0].Exception);
    Assert.IsType<NotSupportedException>(logger.Entries[1].Exception);
  }
}
