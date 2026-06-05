namespace toimi.tools.ruutu.Rendering;

public class RenderException : Exception
{
  public RenderException(string message) : base(message) { }
  public RenderException(string message, Exception inner) : base(message, inner) { }
}
