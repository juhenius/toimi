using toimi.tools.tietue.Handlers;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class HandlerRegistryTests
{
  [Fact]
  public void Resolves_by_kind_and_returns_null_for_unknown()
  {
    var notify = new NotifyHandler(new FakeNotifier());
    var registry = new HandlerRegistry([notify]);

    Assert.Same(notify, registry.Resolve("notify"));
    Assert.Null(registry.Resolve("nope"));
  }
}
