namespace toimi.tools.ruutu.Rendering;

public static class TierBriefs
{
  public const string MODERN = """
    MODERN tier targets Safari 14+, Chrome 90+ (≈2020+).
    Allowed: flexbox, CSS grid, gap, position: sticky, vw/vh/rem/clamp()/min()/max(),
    CSS variables, modern color syntax (rgb(0 0 0 / 50%) and rgba()), system fonts +
    optional web fonts, JPG/PNG/SVG/WebP, @keyframes, transitions, transforms.
    Avoid: :has() (Safari 15.4+, conservative skip).
    Assume responsive viewport between 768 and 1920 px in either orientation.
    Use class names and data-* attributes for interactivity selectors.
    """;

  public const string LEGACY = """
    LEGACY tier targets iOS Safari 9-12 (iPad 2/3/4/Air 1, ≈2015–2018).
    Disallowed (linter will reject): flexbox, CSS grid, var(--*), @import, @font-face,
    WebP images, clamp()/min()/max() CSS functions, :has() / :is() / :where().
    Layout: tables (yes, deliberately), floats, inline-block.
    Units: px / em / % / vw / vh only.
    Colors: hex and rgba() only.
    Fonts: system stack only — do not use @font-face or web fonts.
    Selectors: tag, class, id, :hover. Avoid pseudo-class combinators newer than CSS2.
    Animations: basic @keyframes and transitions only; no 3D transforms.
    Assume viewport ≈ 1024 × 768 in either orientation; design for both.
    Templates are declarative HTML only — no <script> tags (the shell handles interactivity).
    Use data-tap, data-target, data-value attributes on tappable elements.
    """;
}
