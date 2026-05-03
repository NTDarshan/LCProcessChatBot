namespace backend.Dtos;

// Incoming payload sent by the Angular frontend
public class ChatRequestDto
{
    // The user's natural language question
    public string Message { get; set; } = string.Empty;

    // Email used to identify the user in the database
    public string UserEmail { get; set; } = string.Empty;

    // Optional: carry over an existing session; null means create a new one
    public string? SessionId { get; set; }
}
