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
        _chatService    = chatService;
        _historyService = historyService;
        _sessionService = sessionService;
        _logger         = logger;
    }

    // POST /api/chat/query
    // cancellationToken: bound to HttpContext.RequestAborted by ASP.NET Core.
    // The token is propagated through the entire processing pipeline so that
    // Azure OpenAI calls, SQL queries, and SignalR emissions all stop
    // immediately when the client disconnects or aborts the request.
    [HttpPost("query")]
    public async Task<IActionResult> Query(
        [FromBody] ChatRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            // Pass HttpContext.RequestAborted into the service layer so every
            // downstream async call participates in cooperative cancellation.
            var result = await _chatService.ProcessAsync(request, null, cancellationToken);
            return Ok(result);
        }
        catch (OperationCanceledException)
        {
            // Client disconnected or aborted – this is expected, not an error.
            _logger.LogInformation(
                "Request cancelled | Email: {Email} | Message: {Message}",
                request.UserEmail, request.Message);
            // 499 is the de facto "Client Closed Request" status used by many APIs.
            // ASP.NET Core will typically not send a response body since the client
            // is already gone, but returning a meaningful status keeps logs clean.
            return StatusCode(499);
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

    // GET /api/chat/sessions?email=...
    // cancellationToken: bound to HttpContext.RequestAborted.
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(
        [FromQuery] string email,
        CancellationToken cancellationToken)
    {
        try
        {
            var userId   = await _sessionService.GetUserIdAsync(email, cancellationToken);
            var sessions = await _historyService.GetSessionsAsync(userId, cancellationToken);
            return Ok(sessions);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("GetSessions request cancelled | Email: {Email}", email);
            return StatusCode(499);
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

    // GET /api/chat/sessions/{sessionId}/history?email=...
    // cancellationToken: bound to HttpContext.RequestAborted.
    [HttpGet("sessions/{sessionId}/history")]
    public async Task<IActionResult> GetSessionHistory(
        string sessionId,
        [FromQuery] string email,
        CancellationToken cancellationToken)
    {
        try
        {
            var userId  = await _sessionService.GetUserIdAsync(email, cancellationToken);
            var history = await _historyService.GetSessionHistoryAsync(sessionId, userId, cancellationToken);

            var items = history.ToList();
            if (items.Count == 0)
                return NotFound(new { error = "Session not found or contains no messages." });

            return Ok(items);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("GetSessionHistory request cancelled | SessionId: {SessionId}", sessionId);
            return StatusCode(499);
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

    // DELETE /api/chat/sessions/{sessionId}?email=...
    // cancellationToken: bound to HttpContext.RequestAborted.
    [HttpDelete("sessions/{sessionId}")]
    public async Task<IActionResult> DeleteSession(
        string sessionId,
        [FromQuery] string email,
        CancellationToken cancellationToken)
    {
        try
        {
            var userId = await _sessionService.GetUserIdAsync(email, cancellationToken);
            await _historyService.DeleteSessionAsync(sessionId, userId, cancellationToken);
            _logger.LogInformation("Session deleted | SessionId: {SessionId} | Email: {Email}", sessionId, email);
            return NoContent();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("DeleteSession request cancelled | SessionId: {SessionId}", sessionId);
            return StatusCode(499);
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
