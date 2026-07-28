using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using toimi.tools.ruutu.Data.Repositories;
using toimi.tools.ruutu.Transport;
using Xunit;

namespace toimi.tools.ruutu.Tests;

public class DisplayApiControllerTests
{
  [Fact]
  public async Task GetShell_html_encodes_unknown_identifier()
  {
    using var db = TestDb.New();
    var repo = new DisplayRepository(db);
    var controller = new DisplayApiController(
      repo, new FakeWebHostEnvironment(), NullLogger<DisplayApiController>.Instance);

    const string identifier = "<img src=x onerror=alert(1)>";
    var result = await controller.GetShell(identifier, CancellationToken.None);

    var content = Assert.IsType<ContentResult>(result);
    Assert.NotNull(content.Content);
    Assert.DoesNotContain(identifier, content.Content, StringComparison.Ordinal);
    Assert.Contains(WebUtility.HtmlEncode(identifier), content.Content, StringComparison.Ordinal);
  }

  private sealed class FakeWebHostEnvironment : IWebHostEnvironment
  {
    public string WebRootPath { get; set; } = "";
    public IFileProvider WebRootFileProvider { get; set; } = null!;
    public string EnvironmentName { get; set; } = "Test";
    public string ApplicationName { get; set; } = "toimi.tools.ruutu.Tests";
    public string ContentRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = null!;
  }
}
