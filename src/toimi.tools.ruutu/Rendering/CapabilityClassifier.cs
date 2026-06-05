namespace toimi.tools.ruutu.Rendering;

public static class CapabilityClassifier
{
  public static string Classify(CapabilityPayload caps)
  {
    return caps.Flexbox && caps.Fetch && caps.Promise ? "modern" : "legacy";
  }

  public static string DeriveOrientation(int width, int height)
  {
    return height > width ? "portrait" : "landscape";
  }
}
