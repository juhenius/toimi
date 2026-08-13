using System.Text.Json;
using System.Threading.Channels;

namespace toimi.tools.tietue.Webhooks;

/// <summary>
/// One accepted webhook call. OccurrenceUtc is the doorbell contract: the instant minted
/// by the endpoint, returned to the caller in the 202, and used verbatim as the occurrence
/// identity when the dispatcher runs the handler. Params is always a detached object element.
/// </summary>
public sealed record WebhookFiring(Guid TriggerId, DateTimeOffset OccurrenceUtc, JsonElement Params);

/// <summary>Bounded hand-off from the /hooks endpoint to the background dispatcher; full queue → 503 at the edge.</summary>
public class WebhookDispatchChannel
{
  public const int Capacity = 100;

  private readonly Channel<WebhookFiring> _channel = Channel.CreateBounded<WebhookFiring>(Capacity);

  public bool TryEnqueue(WebhookFiring firing)
  {
    return _channel.Writer.TryWrite(firing);
  }

  public ChannelReader<WebhookFiring> Reader => _channel.Reader;
}
