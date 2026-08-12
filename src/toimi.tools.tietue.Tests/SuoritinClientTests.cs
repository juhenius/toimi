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
    return new SuoritinRequest(code, input.RootElement.Clone(), 20000, ["api.example.com"], null);
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
  public async Task Sends_camelcase_payload_with_net_and_no_capability_vocabulary()
  {
    var stub = new StubHandler(/*lang=json,strict*/ """{"ok":true,"effects":{},"logs":[],"error":null,"stats":{"durationMs":1}}""");
    var client = new SuoritinClient(new StubFactory(stub));

    await client.ExecuteAsync(Request("CODE"));

    using var sent = JsonDocument.Parse(stub.LastRequestBody!);
    Assert.Equal("CODE", sent.RootElement.GetProperty("code").GetString());
    Assert.Equal(20000, sent.RootElement.GetProperty("timeoutMs").GetInt32());
    Assert.Equal("api.example.com", sent.RootElement.GetProperty("net")[0].GetString());
    // Grants/allowedHosts/runToken/callbackUrl never cross the seam anymore.
    Assert.False(sent.RootElement.TryGetProperty("grants", out _));
    Assert.False(sent.RootElement.TryGetProperty("allowedHosts", out _));
    // Absent extract is OMITTED, not JSON null (suoritin's null-as-absent
    // tolerance is a backstop, not the contract).
    Assert.False(sent.RootElement.TryGetProperty("extract", out _));
  }

  [Fact]
  public async Task Present_extract_serializes_as_camelcase_url_and_token()
  {
    var stub = new StubHandler(/*lang=json,strict*/ """{"ok":true,"effects":{},"logs":[],"error":null,"stats":{"durationMs":1}}""");
    var client = new SuoritinClient(new StubFactory(stub));
    using var input = JsonDocument.Parse(/*lang=json,strict*/ """{"data":{}}""");

    await client.ExecuteAsync(new SuoritinRequest(
      "CODE", input.RootElement.Clone(), 20000, ["h.example"],
      new ExtractGrant("http://tietue.test/internal/runs/extract", "tok")));

    using var sent = JsonDocument.Parse(stub.LastRequestBody!);
    var extract = sent.RootElement.GetProperty("extract");
    Assert.Equal("http://tietue.test/internal/runs/extract", extract.GetProperty("url").GetString());
    Assert.Equal("tok", extract.GetProperty("token").GetString());
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
