using System.Net;
using toimi.tools.koti.HomeAssistant;
using Xunit;

namespace toimi.tools.koti.Tests;

public class HomeAssistantClientConfigTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public List<Uri> RequestUris { get; } = [];
    public System.Net.Http.Headers.AuthenticationHeaderValue? AuthHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      RequestUris.Add(request.RequestUri!);
      AuthHeader = request.Headers.Authorization;
      return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
      });
    }
  }

  private static async Task<Uri> RequestUriFor(string baseUrl)
  {
    var handler = new StubHandler();
    var client = new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = baseUrl, BearerToken = "token" });

    await client.GetStatesAsync();

    return Assert.Single(handler.RequestUris);
  }

  [Theory]
  [InlineData("http://ha.test:8123")]
  [InlineData("http://ha.test:8123/")]
  public async Task BaseUrl_with_and_without_trailing_slash_yields_the_same_request_uri(string baseUrl)
  {
    var uri = await RequestUriFor(baseUrl);
    Assert.Equal(new Uri("http://ha.test:8123/api/states"), uri);
  }

  [Fact]
  public async Task Sub_path_base_url_is_preserved_in_the_request()
  {
    var uri = await RequestUriFor("http://ha.test:8123/hass");
    Assert.Equal("/hass/api/states", uri.AbsolutePath);
  }

  [Theory]
  [InlineData("Bearer abc", "abc")]
  [InlineData("bearer abc", "abc")]
  [InlineData("abc", "abc")]
  public async Task Bearer_prefix_is_stripped_regardless_of_case(string token, string expectedParameter)
  {
    var handler = new StubHandler();
    var client = new HomeAssistantClient(
      new HttpClient(handler),
      new HomeAssistantOptions { BaseUrl = "http://ha.test:8123", BearerToken = token });

    await client.GetStatesAsync();

    Assert.NotNull(handler.AuthHeader);
    Assert.Equal("Bearer", handler.AuthHeader.Scheme);
    Assert.Equal(expectedParameter, handler.AuthHeader.Parameter);
  }
}
