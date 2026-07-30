using toimi.tools.selain.Browser;
using Xunit;

namespace toimi.tools.selain.Tests;

public class TabManagerTests
{
  private static TabManager Manager(string publicBaseUrl = "https://toimi.example")
  {
    return new TabManager(new SelainOptions { PublicBaseUrl = publicBaseUrl });
  }

  [Fact]
  public void First_adopted_tab_becomes_active()
  {
    var tabs = Manager();
    var id = tabs.Adopt(new FakePageSession());
    Assert.Equal(id, tabs.Active?.Id);
    Assert.Equal(1, tabs.Count);
  }

  [Fact]
  public void Adopting_a_second_tab_does_not_steal_active()
  {
    var tabs = Manager();
    var first = tabs.Adopt(new FakePageSession());
    tabs.Adopt(new FakePageSession());
    Assert.Equal(first, tabs.Active?.Id);
    Assert.Equal(2, tabs.Count);
  }

  [Fact]
  public void Adopting_the_same_native_handle_twice_returns_the_same_id()
  {
    var tabs = Manager();
    var session = new FakePageSession();
    var id = tabs.Adopt(session);
    Assert.Equal(id, tabs.Adopt(session));
    Assert.Equal(1, tabs.Count);
  }

  [Fact]
  public void Switch_changes_active_and_rejects_unknown_ids()
  {
    var tabs = Manager();
    tabs.Adopt(new FakePageSession());
    var second = tabs.Adopt(new FakePageSession());
    Assert.True(tabs.Switch(second));
    Assert.Equal(second, tabs.Active?.Id);
    Assert.False(tabs.Switch(Guid.NewGuid()));
  }

  [Fact]
  public async Task Close_removes_the_tab_closes_the_session_and_falls_back_active()
  {
    var tabs = Manager();
    var session = new FakePageSession();
    var first = tabs.Adopt(session);
    var second = tabs.Adopt(new FakePageSession());
    Assert.True(await tabs.CloseAsync(first));
    Assert.True(session.Closed);
    Assert.Equal(second, tabs.Active?.Id);
    Assert.False(await tabs.CloseAsync(first));
  }

  [Fact]
  public void RemoveByHandle_drops_active_tab_falls_back_and_leaves_session_open()
  {
    var tabs = Manager();
    var first = new FakePageSession();
    var firstId = tabs.Adopt(first);
    var second = tabs.Adopt(new FakePageSession());

    Assert.Equal(firstId, tabs.Active?.Id);
    Assert.True(tabs.RemoveByHandle(first.NativeHandle));
    Assert.Equal(1, tabs.Count);
    Assert.Equal(second, tabs.Active?.Id);
    // Reaping a self-closed page must NOT re-close the session.
    Assert.False(first.Closed);
  }

  [Fact]
  public void RemoveByHandle_returns_false_for_unknown_handle_without_throwing()
  {
    var tabs = Manager();
    tabs.Adopt(new FakePageSession());
    Assert.False(tabs.RemoveByHandle(new object()));
    Assert.Equal(1, tabs.Count);
  }

  [Fact]
  public void ResetAll_clears_everything()
  {
    var tabs = Manager();
    tabs.Adopt(new FakePageSession());
    tabs.ResetAll();
    Assert.Equal(0, tabs.Count);
    Assert.Null(tabs.Active);
  }

  [Fact]
  public void ViewerUrl_composes_from_public_base_url_trimming_slash()
  {
    var tabs = Manager("https://toimi.example/");
    var id = tabs.Adopt(new FakePageSession());
    Assert.Equal($"https://toimi.example/tabs/{id}/view", tabs.ViewerUrl(id));
  }

  [Fact]
  public void Dialog_notes_are_taken_once()
  {
    var tabs = Manager();
    var id = tabs.Adopt(new FakePageSession());
    tabs.NoteDialog(id, "[alert dialog auto-dismissed]");
    Assert.Equal("[alert dialog auto-dismissed]", tabs.TakeDialogNote(id));
    Assert.Null(tabs.TakeDialogNote(id));
  }

  [Fact]
  public void FindByHandle_locates_a_tab_by_its_native_page()
  {
    var tabs = Manager();
    var session = new FakePageSession();
    var id = tabs.Adopt(session);
    Assert.Equal(id, tabs.FindByHandle(session.NativeHandle));
    Assert.Null(tabs.FindByHandle(new object()));
  }
}
