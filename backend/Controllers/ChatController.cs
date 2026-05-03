using backend.Dtos;
using backend.Services;
using Microsoft.AspNetCore.Mvc;

namespace backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ChatController : ControllerBase
{
    private readonly ChatService _chatService;
    private readonly ChatHistoryService _historyService;
    private readonly SessionService _sessionService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(ChatService chatService, ChatHistoryService historyService, SessionService sessionService, ILogger<ChatController> logger)
    {
        _chatService = chatService;
        _historyService = historyService;
        _sessionService = sessionService;
        _logger = logger;
    }

    [HttpPost("query")]
    public async Task<IActionResult> Query([FromBody] ChatRequestDto request)
    {
        try
        {
            var result = await _chatService.ProcessAsync(request);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("Business rule error: {Message}", ex.Message);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error processing chat query");
            return StatusCode(500, new { error = "An internal error occurred. Please try again." });
        }
    }

    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions([FromQuery] string email)
    {
  

        try
        {
            var userId = await _sessionService.GetUserIdAsync(email);
            var sessions = await _historyService.GetSessionsAsync(userId);
            return Ok(sessions);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("GetSessions – user not found: {Email}", email);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error fetching sessions for {Email}", email);
            return StatusCode(500, new { error = "Failed to retrieve sessions." });
        }
    }

    [HttpGet("sessions/{sessionId}/history")]
    public async Task<IActionResult> GetSessionHistory(string sessionId, [FromQuery] string email)
    {

        try
        {
            var userId = await _sessionService.GetUserIdAsync(email);
            var history = await _historyService.GetSessionHistoryAsync(sessionId, userId);

            var items = history.ToList();
            if (items.Count == 0)
                return NotFound(new { error = "Session not found or contains no messages." });

            return Ok(items);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("GetSessionHistory – user not found: {Email}", email);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error fetching history for session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Failed to retrieve session history." });
        }
    }

    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> DeleteSession(string sessionId, [FromQuery] string email)
    {
        try
        {
            var userId = await _sessionService.GetUserIdAsync(email);
            await _historyService.DeleteSessionAsync(sessionId, userId);
            _logger.LogInformation("Session deleted | SessionId: {SessionId} | Email: {Email}", sessionId, email);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("DeleteSession – user not found: {Email}", email);
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error deleting session {SessionId}", sessionId);
            return StatusCode(500, new { error = "Failed to delete session." });
        }
    }
}
