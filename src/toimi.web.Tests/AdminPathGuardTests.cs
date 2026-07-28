using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Toimi.Web.Admin;
using Xunit;

namespace Toimi.Web.Tests;

public class AdminPathGuardTests
{
  private static async Task<TestServer> HostWithGuardAsync()
  {
    var host = await new HostBuilder()
      .ConfigureWebHost(web => web
        .UseTestServer()
        .Configure(app =>
        {
          app.UseAdminPathGuard();
          app.Run(ctx => ctx.Response.WriteAsync("reached:" + ctx.Request.Path));
        }))
      .StartAsync();
    return host.GetTestServer();
  }

  [Theory]
  [InlineData("/admin")]
  [InlineData("/admin/data")]
  [InlineData("/api/admin/summary")]
  [InlineData("/api/admin/tietue/usage")]
  public async Task Exact_lowercase_admin_paths_pass_through(string path)
  {
    var server = await HostWithGuardAsync();
    var resp = await server.CreateClient().GetAsync(path);
    Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
  }

  [Theory]
  [InlineData("/Admin")]
  [InlineData("/ADMIN/data")]
  [InlineData("/Api/admin/summary")]
  [InlineData("/api/Admin/summary")]
  public async Task Non_canonical_case_variant_admin_paths_are_rejected(string path)
  {
    var server = await HostWithGuardAsync();
    var resp = await server.CreateClient().GetAsync(path);
    Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
  }

  [Theory]
  [InlineData("/admin", "/%61dmin")]                       // percent-encoded 'a'
  [InlineData("/api/admin/summary", "/api/%61dmin/summary")]
  [InlineData("/Admin", "/%41dmin")]                       // percent-encoded 'A'
  public async Task Percent_encoded_admin_paths_are_rejected(string decodedPath, string rawTarget)
  {
    // The TestServer HttpClient can normalize percent-encodings before the
    // middleware sees them, so drive decoded path and raw target independently
    // — exactly the split a real server presents for these requests.
    var server = await HostWithGuardAsync();
    var ctx = await server.SendAsync(c =>
    {
      c.Request.Method = "GET";
      c.Request.Path = decodedPath;
      c.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
    });
    Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
  }

  [Theory]
  [InlineData("/")]
  [InlineData("/toimihub")]
  [InlineData("/administrivia-page")]   // not an /admin segment — decoded segment check must not match
  [InlineData("/health")]
  public async Task Unrelated_paths_pass_through(string path)
  {
    var server = await HostWithGuardAsync();
    var resp = await server.CreateClient().GetAsync(path);
    Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
  }
}
