using toimi.tools.ruutu.Rendering;
using Xunit;

namespace toimi.tools.ruutu.Tests.Rendering;

public class CapabilityClassifierTests
{
  private static CapabilityPayload Caps(bool flex, bool fetch, bool promise)
  {
    return new(flex, CssGrid: flex, Fetch: fetch, Promise: promise,
        ViewportWidth: 1024, ViewportHeight: 768, UserAgent: "Test");
  }

  [Fact]
  public void Classifies_modern_when_all_features_present()
  {
    Assert.Equal("modern", CapabilityClassifier.Classify(Caps(true, true, true)));
  }

  [Fact]
  public void Classifies_legacy_when_fetch_missing()
  {
    Assert.Equal("legacy", CapabilityClassifier.Classify(Caps(true, false, true)));
  }

  [Fact]
  public void Classifies_legacy_when_flexbox_missing()
  {
    Assert.Equal("legacy", CapabilityClassifier.Classify(Caps(false, true, true)));
  }

  [Fact]
  public void Classifies_legacy_when_promise_missing()
  {
    Assert.Equal("legacy", CapabilityClassifier.Classify(Caps(true, true, false)));
  }

  [Fact]
  public void Derives_orientation_landscape_when_width_gt_height()
  {
    Assert.Equal("landscape", CapabilityClassifier.DeriveOrientation(1024, 768));
  }

  [Fact]
  public void Derives_orientation_portrait_when_height_gt_width()
  {
    Assert.Equal("portrait", CapabilityClassifier.DeriveOrientation(768, 1024));
  }

  [Fact]
  public void Derives_orientation_landscape_on_square()
  {
    Assert.Equal("landscape", CapabilityClassifier.DeriveOrientation(1000, 1000));
  }
}
