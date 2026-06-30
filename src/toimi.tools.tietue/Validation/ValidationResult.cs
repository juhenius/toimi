namespace toimi.tools.tietue.Validation;

public record ValidationResult(bool IsValid, IReadOnlyList<string> Errors)
{
  public static ValidationResult Valid()
  {
    return new(true, []);
  }

  public static ValidationResult Invalid(IReadOnlyList<string> errors)
  {
    return new(false, errors);
  }

  public static ValidationResult Invalid(string error)
  {
    return new(false, [error]);
  }
}
