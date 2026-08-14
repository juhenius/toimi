using System.Threading.Channels;

namespace toimi.tools.ruutu.Transport;

/// <summary>
/// One resolved action forward, queued off the event request path. The shell's
/// event POST is fire-and-forget, so the forward must ride neither its latency
/// nor its cancellation token (ADR 0002).
/// </summary>
public sealed record ActionForward(string Identifier, long EventId, string Url);

/// <summary>
/// Bounded hand-off from PostEvent to the ActionForwardWorker. A full queue
/// drops the forward with a log — taps arrive at human rates, so a backlog
/// this deep means tietue is down and the forwards would fail anyway.
/// </summary>
public class ActionForwardChannel
{
  public const int Capacity = 100;

  private readonly Channel<ActionForward> _channel = Channel.CreateBounded<ActionForward>(Capacity);

  public bool TryEnqueue(ActionForward forward)
  {
    return _channel.Writer.TryWrite(forward);
  }

  public ChannelReader<ActionForward> Reader => _channel.Reader;
}
