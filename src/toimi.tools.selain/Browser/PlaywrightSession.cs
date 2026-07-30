using Microsoft.Playwright;

namespace toimi.tools.selain.Browser;

public sealed class PlaywrightSession(IPage page) : IPageSession
{
  public IPage Page { get; } = page;
  public object NativeHandle => Page;
  public string Url => Page.Url;

  public Task<string> TitleAsync()
  {
    return Page.TitleAsync();
  }

  public Task CloseAsync()
  {
    return Page.CloseAsync();
  }
}
