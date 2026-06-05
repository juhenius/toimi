namespace toimi.tools.ruutu.Rendering;

public record LintIssue(int Line, string Rule, string Message);

public record LintResult(bool Valid, IReadOnlyList<LintIssue> Issues)
{
  public static LintResult Ok() => new(true, Array.Empty<LintIssue>());
  public static LintResult Failed(IReadOnlyList<LintIssue> issues) => new(false, issues);
}
