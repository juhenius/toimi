using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using Xunit;

namespace Toimi.Core.Tests;

public class ResilientMcpToolTests
{
  private sealed class ThrowingFunction(Exception ex) : AIFunction
  {
    public override string Name => "probe";

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
      throw ex;
    }
  }

  private sealed class CapturingLogger : ILogger
  {
    public List<string> Messages { get; } = [];

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
      Messages.Add(formatter(state, exception));
    }
  }

  private static ResilientMcpTool NewTool(Exception ex, CapturingLogger logger)
  {
    return new ResilientMcpTool(new McpToolAggregator(), "srv", new ThrowingFunction(ex), logger);
  }

  [Fact]
  public async Task Cancellation_rethrows_without_attempting_reconnect()
  {
    // A bare OperationCanceledException fails IsTransportFault on its own merits
    // (it isn't one of the listed types and has no InnerException to walk), so
    // this alone does not prove the dedicated `catch (OperationCanceledException)
    // { throw; }` block is load-bearing — see
    // Cancellation_wrapping_a_transport_fault_still_short_circuits below for that.
    var logger = new CapturingLogger();
    var tool = NewTool(new OperationCanceledException(), logger);

    await Assert.ThrowsAsync<OperationCanceledException>(
      () => tool.InvokeAsync([], CancellationToken.None).AsTask());

    Assert.DoesNotContain(logger.Messages, m => m.Contains("reconnecting", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Cancellation_wrapping_a_transport_fault_still_short_circuits()
  {
    // Real-world shape: a cancelled MCP call whose OperationCanceledException
    // wraps the dying channel's exception. IsTransportFault's InnerException
    // walk WOULD match this via the McpException inner, so only the dedicated
    // `catch (OperationCanceledException) { throw; }` block (checked before the
    // transport-fault filter) prevents a reconnect attempt here. Without that
    // block, every cancelled/timed-out call in this shape would hammer reconnects.
    var logger = new CapturingLogger();
    var tool = NewTool(new OperationCanceledException("cancelled", new McpException("channel closed")), logger);

    await Assert.ThrowsAsync<OperationCanceledException>(
      () => tool.InvokeAsync([], CancellationToken.None).AsTask());

    Assert.DoesNotContain(logger.Messages, m => m.Contains("reconnecting", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Transport_fault_attempts_reconnect_then_surfaces_original()
  {
    var logger = new CapturingLogger();
    var tool = NewTool(new HttpRequestException("boom"), logger);

    await Assert.ThrowsAsync<HttpRequestException>(
      () => tool.InvokeAsync([], CancellationToken.None).AsTask());

    Assert.Contains(logger.Messages, m => m.Contains("reconnecting", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Non_transport_exception_passes_through_without_reconnect()
  {
    var logger = new CapturingLogger();
    var tool = NewTool(new InvalidOperationException("nope"), logger);

    await Assert.ThrowsAsync<InvalidOperationException>(
      () => tool.InvokeAsync([], CancellationToken.None).AsTask());

    Assert.DoesNotContain(logger.Messages, m => m.Contains("reconnecting", StringComparison.Ordinal));
  }

  [Fact]
  public async Task Wrapped_transport_fault_still_classifies()
  {
    var logger = new CapturingLogger();
    // IsTransportFault walks the InnerException chain; a plain Exception
    // wrapping an McpException guarantees that chain (unlike AggregateException,
    // whose InnerException is only the first of possibly many InnerExceptions).
#pragma warning disable CA2201 // deliberately generic wrapper to exercise the InnerException walk
    var tool = NewTool(new Exception("outer", new McpException("inner")), logger);
#pragma warning restore CA2201

    await Assert.ThrowsAsync<Exception>(
      () => tool.InvokeAsync([], CancellationToken.None).AsTask());

    Assert.Contains(logger.Messages, m => m.Contains("reconnecting", StringComparison.Ordinal));
  }
}
