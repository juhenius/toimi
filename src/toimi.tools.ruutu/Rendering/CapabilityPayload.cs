namespace toimi.tools.ruutu.Rendering;

public record CapabilityPayload(
  bool Flexbox,
  bool CssGrid,
  bool Fetch,
  bool Promise,
  int ViewportWidth,
  int ViewportHeight,
  string UserAgent);
