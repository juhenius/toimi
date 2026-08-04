using Microsoft.Extensions.Time.Testing;
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class RunTokenStoreTests
{
  [Fact]
  public void Issued_token_with_llm_grant_allows_extract()
  {
    var store = new RunTokenStore();
    var token = store.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    Assert.True(store.TryUseExtract(token));
  }

  [Fact]
  public void Token_without_llm_grant_is_rejected()
  {
    var store = new RunTokenStore();
    var token = store.Issue(Guid.NewGuid(), ["setField"], TimeSpan.FromMinutes(1));
    Assert.False(store.TryUseExtract(token));
  }

  [Fact]
  public void Unknown_token_is_rejected()
  {
    Assert.False(new RunTokenStore().TryUseExtract("nope"));
  }

  [Fact]
  public void Expired_token_is_rejected()
  {
    var time = new FakeTimeProvider();
    var store = new RunTokenStore(time);
    var token = store.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromSeconds(30));
    time.Advance(TimeSpan.FromSeconds(31));
    Assert.False(store.TryUseExtract(token));
  }

  [Fact]
  public void Call_budget_is_enforced()
  {
    var store = new RunTokenStore();
    var token = store.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    for (var i = 0; i < RunTokenStore.MaxExtractCalls; i++)
    {
      Assert.True(store.TryUseExtract(token));
    }

    Assert.False(store.TryUseExtract(token));
  }

  [Fact]
  public void Revoked_token_is_rejected()
  {
    var store = new RunTokenStore();
    var token = store.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    store.Revoke(token);
    Assert.False(store.TryUseExtract(token));
  }
}
