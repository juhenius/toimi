namespace toimi.tools.selain.Browser;

/// <summary>
/// The slice of a browser page TabManager needs for bookkeeping. PlaywrightSession
/// wraps a real IPage; tests use FakePageSession. NativeHandle lets the popup
/// adoption path dedupe (the context Page event and NewPageAsync both see the
/// same underlying page object).
/// </summary>
public interface IPageSession
{
  object NativeHandle { get; }
  string Url { get; }
  Task<string> TitleAsync();
  Task CloseAsync();
}
