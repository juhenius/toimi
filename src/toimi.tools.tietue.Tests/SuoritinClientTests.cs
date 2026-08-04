using System.Net;
using System.Text;
using System.Text.Json;
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class SuoritinClientTests
{
  private sealed class StubHandler(string responseJson) : HttpMessageHandler
  {
    public string? LastRequestBody;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
      LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
      return new HttpResponseMessage(HttpStatusCode.OK)
      {
        Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
      };
    }
  }

  private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
  {
    public HttpClient CreateClient(string name)
    {
      return new HttpClient(handler) { BaseAddress = new Uri("http://suoritin.test") };
    }
  }

  private static SuoritinRequest Request(string code = "export default () => ({})")
  {
    using var input = JsonDocument.Parse(/*lang=json,strict*/ """{"data":{}}""");
    return new SuoritinRequest(code, input.RootElement.Clone(), 20000, ["api.example.com"], ["setField"], null, null);
  }

  [Fact]
  public async Task Parses_success_response()
  {
    var stub = new StubHandler(/*lang=json,strict*/
      """{"ok":true,"effects":{"setField":[{"path":"a","value":1}]},"logs":["[log] hi"],"error":null,"stats":{"durationMs":12}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    var result = await client.ExecuteAsync(Request());

    Assert.True(result.Ok);
    Assert.Contains("setField", result.EffectsJson);
    Assert.Equal("[log] hi", Assert.Single(result.Logs));
    Assert.Null(result.Error);
    Assert.Equal(12, result.DurationMs);
  }

  [Fact]
  public async Task Parses_failure_response()
  {
    var stub = new StubHandler(/*lang=json,strict*/
      """{"ok":false,"effects":null,"logs":[],"error":"boom","stats":{"durationMs":5}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    var result = await client.ExecuteAsync(Request());

    Assert.False(result.Ok);
    Assert.Null(result.EffectsJson);
    Assert.Equal("boom", result.Error);
  }

  [Fact]
  public async Task Sends_camelcase_payload_with_all_fields()
  {
    var stub = new StubHandler(/*lang=json,strict*/ """{"ok":true,"effects":{},"logs":[],"error":null,"stats":{"durationMs":1}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    await client.ExecuteAsync(Request("CODE"));

    using var sent = JsonDocument.Parse(stub.LastRequestBody!);
    Assert.Equal("CODE", sent.RootElement.GetProperty("code").GetString());
    Assert.Equal(20000, sent.RootElement.GetProperty("timeoutMs").GetInt32());
    Assert.Equal("api.example.com", sent.RootElement.GetProperty("allowedHosts")[0].GetString());
    Assert.Equal("setField", sent.RootElement.GetProperty("grants")[0].GetString());
  }

  [Fact]
  public async Task Caps_log_count_and_entry_length()
  {
    var longLog = "\"" + new string('a', SuoritinClient.MaxLogChars + 500) + "\"";
    var rest = string.Join(",", Enumerable.Range(0, 105).Select(i => $"\"log{i}\""));
    var stub = new StubHandler(
      $$$"""{"ok":true,"effects":{},"logs":[{{{longLog}}},{{{rest}}}],"error":null,"stats":{"durationMs":1}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    var result = await client.ExecuteAsync(Request());

    Assert.Equal(SuoritinClient.MaxLogEntries, result.Logs.Length);
    Assert.Equal(SuoritinClient.MaxLogChars + 1, result.Logs[0].Length);
    Assert.EndsWith("…", result.Logs[0]);
  }

  [Fact]
  public async Task Oversized_effects_payload_is_a_failure()
  {
    var big = new string('x', SuoritinClient.MaxEffectsBytes + 16);
    var stub = new StubHandler(
      $$$"""{"ok":true,"effects":{"setField":[{"path":"a","value":"{{{big}}}"}]},"logs":["kept"],"error":null,"stats":{"durationMs":7}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    var result = await client.ExecuteAsync(Request());

    Assert.False(result.Ok);
    Assert.Null(result.EffectsJson);
    Assert.Equal("effects payload exceeds tietue-side cap", result.Error);
    Assert.Equal("kept", Assert.Single(result.Logs));
    Assert.Equal(7, result.DurationMs);
  }
}
