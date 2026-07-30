using toimi.tools.selain.Browser;

namespace toimi.tools.selain.Tests;

public sealed class FakePageSession(string url = "about:blank", string title = "fake") : IPageSession
{
  public bool Closed { get; private set; }
  public object NativeHandle => this;
  public string Url => url;

  public Task<string> TitleAsync()
  {
    return Task.FromResult(title);
  }

  public Task CloseAsync()
  {
    Closed = true;
    return Task.CompletedTask;
  }
}
