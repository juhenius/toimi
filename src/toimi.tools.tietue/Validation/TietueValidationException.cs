namespace toimi.tools.tietue.Validation;

public class TietueValidationException(IReadOnlyList<string> errors)
  : Exception(string.Join("; ", errors))
{
  public IReadOnlyList<string> Errors { get; } = errors;
}
