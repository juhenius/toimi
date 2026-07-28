using System.Collections.Concurrent;
using System.Threading.Channels;

namespace toimi.tools.ruutu.Transport;

public sealed class SseHub
{
  private readonly ConcurrentDictionary<string, Channel<SseEvent>> _channels = new();

  public Channel<SseEvent> Subscribe(string identifier)
  {
    var newChan = Channel.CreateBounded<SseEvent>(new BoundedChannelOptions(64)
    {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = false
    });
    return _channels.AddOrUpdate(identifier, newChan, (_, existing) =>
    {
      existing.Writer.TryComplete();
      return newChan;
    });
  }

  public void Unsubscribe(string identifier, Channel<SseEvent> channel)
  {
    if (_channels.TryGetValue(identifier, out var current) && current == channel)
    {
      _channels.TryRemove(identifier, out _);
      channel.Writer.TryComplete();
    }
  }

  public async Task<bool> PublishAsync(string identifier, SseEvent ev, CancellationToken ct = default)
  {
    if (!_channels.TryGetValue(identifier, out var ch))
    {
      return false;
    }

    await ch.Writer.WriteAsync(ev, ct);
    return true;
  }

  public bool HasSubscriber(string identifier)
  {
    return _channels.ContainsKey(identifier);
  }
}
