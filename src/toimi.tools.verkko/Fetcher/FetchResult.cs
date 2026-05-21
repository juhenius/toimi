namespace toimi.tools.verkko.Fetcher;

public record FetchResult(string Url, int StatusCode, string ContentType, string Content);
