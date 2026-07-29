using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Toimi.Notifications.Tests;

public class NtfyClientTests
{
  private sealed class StubHandler : HttpMessageHandler
  {
    public List<string> Bodies { get; } = [];
    public List<AuthenticationHeaderValue?> AuthHeaders { get; } = [];
    public List<Uri?> RequestUris { get; } = [];
    public HttpStatusCode ResponseStatusCode { get; set; } = HttpStatusCode.OK;
    public string? ResponseBody { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
      AuthHeaders.Add(request.Headers.Authorization);
      RequestUris.Add(request.RequestUri);
      var response = new HttpResponseMessage(ResponseStatusCode);
      if (ResponseBody is not null)
      {
        response.Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json");
      }
      return response;
    }
  }

  private static async Task<int> SentPriority(string priority)
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message", priority: priority);

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    return doc.RootElement.GetProperty("priority").GetInt32();
  }

  [Theory]
  [InlineData("min", 1)]
  [InlineData("low", 2)]
  [InlineData("default", 3)]
  [InlineData("high", 4)]
  [InlineData("urgent", 5)]
  [InlineData("High", 4)]
  [InlineData("URGENT", 5)]
  [InlineData("Default", 3)]
  public async Task Priority_maps_case_insensitively(string priority, int expected)
  {
    // tietue's NotifyHandler passes user-authored behavior config straight through;
    // "High" or "URGENT" silently downgrading to normal means an urgent alert never
    // breaks through Do Not Disturb.
    Assert.Equal(expected, await SentPriority(priority));
  }

  [Theory]
  [InlineData("bogus")]
  [InlineData("")]
  public async Task Unknown_priority_falls_back_to_normal(string priority)
  {
    Assert.Equal(3, await SentPriority(priority));
  }

  [Fact]
  public async Task Title_key_is_absent_from_payload_when_null()
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message", title: null);

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    Assert.False(doc.RootElement.TryGetProperty("title", out _));
  }

  [Fact]
  public async Task Tags_key_is_absent_from_payload_when_null()
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message", tags: null);

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    Assert.False(doc.RootElement.TryGetProperty("tags", out _));
  }

  [Fact]
  public async Task Tags_are_split_on_comma_and_trimmed()
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message", tags: "package, delivered");

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    var tags = doc.RootElement.GetProperty("tags").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    Assert.Equal(["package", "delivered"], tags);
  }

  [Fact]
  public async Task Topic_always_comes_from_options()
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "my-topic" },
      new HttpClient(handler));

    await client.SendAsync("message");

    using var doc = JsonDocument.Parse(Assert.Single(handler.Bodies));
    Assert.Equal("my-topic", doc.RootElement.GetProperty("topic").GetString());
  }

  [Theory]
  [InlineData("http://ntfy.test")]
  [InlineData("http://ntfy.test/")]
  public async Task BaseUrl_with_and_without_trailing_slash_posts_to_the_same_url(string baseUrl)
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = baseUrl, Topic = "toimi" },
      new HttpClient(handler));

    await client.SendAsync("message");

    Assert.Equal(new Uri("http://ntfy.test"), Assert.Single(handler.RequestUris));
  }

  [Theory]
  [InlineData("user", "pass", true)]
  [InlineData("user", null, false)]
  [InlineData(null, "pass", false)]
  [InlineData("", "", false)]
  public async Task Auth_header_present_only_when_both_username_and_password_are_non_empty(
    string? username, string? password, bool expectHeader)
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi", Username = username, Password = password },
      new HttpClient(handler));

    await client.SendAsync("message");

    var header = Assert.Single(handler.AuthHeaders);
    if (expectHeader)
    {
      Assert.NotNull(header);
      Assert.Equal("Basic", header.Scheme);
      Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")), header.Parameter);
    }
    else
    {
      Assert.Null(header);
    }
  }

  [Fact]
  public async Task Utf8_credentials_round_trip_through_base64_as_utf8()
  {
    var handler = new StubHandler();
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi", Username = "käyttäjä", Password = "salasana" },
      new HttpClient(handler));

    await client.SendAsync("message");

    var header = Assert.Single(handler.AuthHeaders);
    Assert.NotNull(header);
    Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("käyttäjä:salasana")), header.Parameter);
  }

  [Fact]
  public async Task Error_response_includes_ntfy_diagnostic_body()
  {
    var handler = new StubHandler
    {
      ResponseStatusCode = HttpStatusCode.Forbidden,
      ResponseBody = /*lang=json,strict*/ """{"code":40301,"error":"forbidden"}""",
    };
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("message"));

    Assert.Contains("forbidden", ex.Message);
  }

  [Fact]
  public async Task Error_body_is_truncated_so_it_cannot_flood_the_event_log()
  {
    // The message is serialized into tietue's EntityEvent.Result (jsonb) by SchedulerTick.
    // A proxy returning a large HTML error page must not land whole in the database.
    var handler = new StubHandler
    {
      ResponseStatusCode = HttpStatusCode.BadGateway,
      ResponseBody = new string('x', 10_000),
    };
    var client = new NtfyClient(
      new NtfyOptions { BaseUrl = "http://ntfy.test", Topic = "toimi" },
      new HttpClient(handler));

    var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync("m"));

    Assert.Contains("502", ex.Message);
    Assert.True(ex.Message.Length < 1000, $"message was {ex.Message.Length} chars; expected a truncated body");
    Assert.Contains("truncated", ex.Message, StringComparison.OrdinalIgnoreCase);
  }
}
