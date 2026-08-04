using toimi.tools.tietue.Scripts;

namespace toimi.tools.tietue.Tests;

public class FakeSuoritinClient : ISuoritinClient
{
  public List<SuoritinRequest> Requests { get; } = [];
  public SuoritinResult NextResult { get; set; } = new(true, "{}", [], null, 1);
  public Exception? NextException { get; set; }

  /// <summary>When set, every call hangs until the token cancels (simulates a hung suoritin connection).</summary>
  public bool Hang { get; set; }

  public async Task<SuoritinResult> ExecuteAsync(SuoritinRequest request, CancellationToken ct = default)
  {
    Requests.Add(request);
    if (Hang)
    {
      await Task.Delay(Timeout.Infinite, ct);
    }

    return NextException is not null ? throw NextException : NextResult;
  }
}
