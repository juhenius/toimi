namespace toimi.tools.ruutu.Transport;

public record SseEvent(string EventType, string JsonPayload);
