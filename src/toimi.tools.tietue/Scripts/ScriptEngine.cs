using Jint;

namespace toimi.tools.tietue.Scripts;

public class ScriptEngine
{
  public string Evaluate(string source, string dataJson)
  {
    try
    {
      var engine = new Engine(options => options
        .TimeoutInterval(TimeSpan.FromSeconds(2))
        .LimitMemory(8_000_000)
        .MaxStatements(10_000)
        .LimitRecursion(100)
        .Strict());

      engine.SetValue("__dataJson", dataJson);
      var wrapped = $"JSON.stringify(((data) => {{ {source} }})(JSON.parse(__dataJson)) || {{}})";
      var result = engine.Evaluate(wrapped);
      if (!result.IsString())
      {
        return "{}";
      }

      var json = result.AsString();
      return json.TrimStart().StartsWith('{') ? json : "{}";
    }
    catch (Exception)
    {
      // Sandbox must be fail-safe: any script error/timeout/limit → no effects.
      return "{}";
    }
  }
}
