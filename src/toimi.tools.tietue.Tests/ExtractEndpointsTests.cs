using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using toimi.tools.tietue.Scripts;
using Xunit;

namespace toimi.tools.tietue.Tests;

public class ExtractEndpointsTests
{
  private static ExtractRequest Request(string? prompt = "get price", string? text = "some html", string? schema = null)
  {
    JsonElement? schemaEl = null;
    if (schema is not null)
    {
      using var doc = JsonDocument.Parse(schema);
      schemaEl = doc.RootElement.Clone();
    }

    return new ExtractRequest(prompt, text, schemaEl);
  }

  [Fact]
  public async Task Valid_token_returns_extracted_json()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor { NextResult = /*lang=json,strict*/ """{"price":19.9}""" };

    var result = await ExtractEndpoints.HandleAsync(
      token, Request(schema: /*lang=json,strict*/ """{"type":"object"}"""), tokens, extractor, default);

    var content = Assert.IsType<ContentHttpResult>(result);
    Assert.Equal(/*lang=json,strict*/ """{"price":19.9}""", content.ResponseContent);
    var (prompt, _, schemaJson) = Assert.Single(extractor.Calls);
    Assert.Equal("get price", prompt);
    Assert.Contains("object", schemaJson, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Invalid_token_is_403()
  {
    var result = await ExtractEndpoints.HandleAsync("bad", Request(), new RunTokenStore(), new FakeLlmExtractor(), default);
    Assert.Equal(403, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public async Task Missing_prompt_or_text_is_400()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));

    var result = await ExtractEndpoints.HandleAsync(token, Request(prompt: null), tokens, new FakeLlmExtractor(), default);

    Assert.IsType<BadRequest<string>>(result);
  }

  [Fact]
  public async Task Non_json_model_output_is_502()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor { NextResult = null };

    var result = await ExtractEndpoints.HandleAsync(token, Request(), tokens, extractor, default);

    Assert.Equal(502, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public async Task Oversized_prompt_is_rejected_with_400()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor();

    var result = await ExtractEndpoints.HandleAsync(
      token, Request(prompt: new string('p', ExtractEndpoints.MaxPromptChars + 1)), tokens, extractor, default);

    var bad = Assert.IsType<BadRequest<string>>(result);
    Assert.Contains("prompt", bad.Value, StringComparison.Ordinal);
    Assert.Empty(extractor.Calls);
  }

  [Fact]
  public async Task Oversized_schema_is_rejected_with_400()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor();
    var schema = $$"""{"description":"{{new string('s', ExtractEndpoints.MaxSchemaChars)}}"}""";

    var result = await ExtractEndpoints.HandleAsync(token, Request(schema: schema), tokens, extractor, default);

    var bad = Assert.IsType<BadRequest<string>>(result);
    Assert.Contains("schema", bad.Value, StringComparison.Ordinal);
    Assert.Empty(extractor.Calls);
  }

  [Fact]
  public async Task Provider_exception_is_502_not_unhandled()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor { NextException = new InvalidOperationException("provider down") };

    var result = await ExtractEndpoints.HandleAsync(token, Request(), tokens, extractor, default);

    Assert.Equal(502, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
  }

  [Fact]
  public async Task Bad_requests_consume_the_call_budget()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor();

    for (var i = 0; i < RunTokenStore.MaxExtractCalls; i++)
    {
      Assert.IsType<BadRequest<string>>(await ExtractEndpoints.HandleAsync(token, Request(prompt: null), tokens, extractor, default));
    }

    var result = await ExtractEndpoints.HandleAsync(token, Request(), tokens, extractor, default);

    Assert.Equal(403, Assert.IsType<StatusCodeHttpResult>(result).StatusCode);
    Assert.Empty(extractor.Calls);
  }

  [Fact]
  public async Task Oversized_text_is_truncated_before_extraction()
  {
    var tokens = new RunTokenStore();
    var token = tokens.Issue(Guid.NewGuid(), ["llm"], TimeSpan.FromMinutes(1));
    var extractor = new FakeLlmExtractor();

    await ExtractEndpoints.HandleAsync(token, Request(text: new string('x', 150_000)), tokens, extractor, default);

    Assert.Equal(ExtractEndpoints.MaxTextChars, Assert.Single(extractor.Calls).Text.Length);
  }
}
