namespace backend.Dtos;

// Session summary returned by GET /api/chat/sessions
public class ChatSessionDto
{
    public string SessionId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    // First user message of the session used as the title in the sidebar
    public string? Title { get; set; }
}

// A single chat turn returned by GET /api/chat/sessions/{sessionId}/history
public class ChatHistoryItemDto
{
    public string Role { get; set; } = string.Empty;   // "user" or "assistant"
    public string Content { get; set; } = string.Empty;
    // Parameterized SQL stored when the message was first generated; null for user messages
    public string? ExecutedQuery { get; set; }
    public string? Intent { get; set; }
    public string? ResponseType { get; set; }
    public string? QueryType { get; set; }
    public string? Data { get; set; } // JSON string of the rows
    public DateTime CreatedAt { get; set; }
}
