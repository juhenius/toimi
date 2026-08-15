using Toimi.Core.Configuration;
using Toimi.Core.Llm;
using Xunit;

namespace Toimi.Core.Tests;

public class ModelTierConfigTests
{
  private static ToimiConfiguration Config(string fast = "fast-m", string? smart = null)
  {
    return new ToimiConfiguration
    {
      OpenAI = new OpenAIOptions { ApiKey = "k", FastModel = fast, SmartModel = smart },
      FastPriceInputPer1M = 1m,
      FastPriceOutputPer1M = 2m,
      SmartPriceInputPer1M = 10m,
      SmartPriceOutputPer1M = 20m,
    };
  }

  [Fact]
  public void Smart_resolves_to_fast_when_unconfigured()
  {
    ILlmClientProvider provider = new OpenAiLlmClientProvider(Config());

    Assert.Equal("fast-m", provider.ResolveModel(ModelTier.Smart));
    Assert.False(provider.HasDistinctSmartModel);
  }

  [Fact]
  public void Blank_smart_model_counts_as_unconfigured()
  {
    ILlmClientProvider provider = new OpenAiLlmClientProvider(Config(smart: "  "));

    Assert.Equal("fast-m", provider.ResolveModel(ModelTier.Smart));
    Assert.False(provider.HasDistinctSmartModel);
  }

  [Fact]
  public void Distinct_smart_model_resolves_per_tier()
  {
    ILlmClientProvider provider = new OpenAiLlmClientProvider(Config(smart: "smart-m"));

    Assert.Equal("fast-m", provider.ResolveModel(ModelTier.Fast));
    Assert.Equal("smart-m", provider.ResolveModel(ModelTier.Smart));
    Assert.True(provider.HasDistinctSmartModel);
  }

  [Fact]
  public void Smart_model_equal_to_fast_is_not_distinct()
  {
    ILlmClientProvider provider = new OpenAiLlmClientProvider(Config(smart: "fast-m"));

    Assert.False(provider.HasDistinctSmartModel);
  }

  [Fact]
  public void Smart_model_equal_to_fast_prices_as_fast()
  {
    // Pricing and the provider's "smart really exists" predicate must agree:
    // a collapsed tier bills fast everywhere.
    var config = Config(smart: "fast-m");

    Assert.Equal((1m, 2m), config.PricesForModel("fast-m"));
  }

  [Theory]
  [InlineData(null, true, ModelTier.Fast)]
  [InlineData("fast", true, ModelTier.Fast)]
  [InlineData("SMART", true, ModelTier.Smart)]
  [InlineData("cheap", false, ModelTier.Fast)]
  public void ModelTiers_parse_is_the_single_vocabulary(string? value, bool valid, ModelTier expected)
  {
    Assert.Equal(valid, ModelTiers.TryParse(value, out var parsed));
    Assert.Equal(expected, parsed);
    Assert.Equal(expected, ModelTiers.ParseOrFast(value));
  }

  [Fact]
  public void Prices_key_by_tier_with_fast_as_the_fallback()
  {
    var config = Config(smart: "smart-m");

    Assert.Equal((10m, 20m), config.PricesForModel("smart-m"));
    Assert.Equal((1m, 2m), config.PricesForModel("fast-m"));
    Assert.Equal((1m, 2m), config.PricesForModel("some-old-model"));
    Assert.Equal((1m, 2m), config.PricesForModel(null));
  }

  [Fact]
  public void Without_a_smart_model_everything_prices_fast()
  {
    var config = Config();

    Assert.Equal((1m, 2m), config.PricesForModel("smart-m"));
    Assert.Equal((1m, 2m), config.PricesForModel(null));
  }
}
