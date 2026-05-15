using backend.Dtos;
using backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace backend.Hubs;

[Authorize]
public class LcChatHub : Hub
{
    private readonly ILogger<LcChatHub> _logger;
    private readonly ChatService _chatService;

    public LcChatHub(ILogger<LcChatHub> logger, ChatService chatService)
    {
        _logger     = logger;
        _chatService = chatService;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR connected | ConnectionId: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Client disconnect is expected behaviour (e.g. tab close, navigation away).
        // Log as informational so monitoring dashboards do not alert on normal UX events.
        _logger.LogInformation(
            "SignalR disconnected | ConnectionId: {ConnectionId} | Reason: {Reason}",
            Context.ConnectionId,
            exception?.Message ?? "clean disconnect");

        return base.OnDisconnectedAsync(exception);
    }

    // Primary SignalR hub method – invoked by the Angular client via connection.invoke("SendMessage").
    //
    // Context.ConnectionAborted is an ASP.NET Core CancellationToken that fires when:
    //   • the WebSocket connection closes (tab close, browser refresh, navigate away)
    //   • the client explicitly disconnects
    //   • network interruption is detected
    //
    // This token is propagated into the entire ChatService pipeline so every
    // downstream async call (OpenAI, Dapper SQL, stage emissions) cancels cooperatively.
    public async Task SendMessage(string userEmail, string message, string? sessionId)
    {
        // Use the hub's built-in ConnectionAborted token as the cancellation source.
        // This fires automatically for all "stop generating" scenarios on the SignalR path.
        var cancellationToken = Context.ConnectionAborted;

        var request = new ChatRequestDto
        {
            UserEmail = userEmail,
            Message   = message,
            SessionId = sessionId
        };

        // Notify client that server received the message
        await Clients.Caller.SendAsync("MessageReceived", cancellationToken: cancellationToken);

        // Stage update callback – guard against cancelled/closed connections before emitting.
        async Task EmitStageAsync(ProcessingStageUpdateDto stage)
        {
            // Stop emitting if the connection is gone – prevents orphaned background sends.
            if (cancellationToken.IsCancellationRequested) return;

            try
            {
                await Clients.Caller.SendAsync("ProcessingStage", stage, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Connection closed during emission – expected, not an error.
                _logger.LogInformation(
                    "Stage emission cancelled | ConnectionId: {ConnectionId} | Stage: {Stage}",
                    Context.ConnectionId, stage.StageKey);
            }
            catch (Exception ex)
            {
                // Keep pipeline resilient even if the socket drops mid-stream.
                _logger.LogDebug(ex, "Failed to stream processing stage for {ConnectionId}", Context.ConnectionId);
            }
        }

        try
        {
            // Pass cancellationToken into ChatService so the full pipeline
            // (OpenAI, SQL, history persist) cancels when the connection drops.
            var result = await _chatService.ProcessAsync(request, EmitStageAsync, cancellationToken);

            // text_only responses signal a business-rule rejection (domain guard, errors).
            if (string.Equals(result.ResponseType, "text_only", StringComparison.OrdinalIgnoreCase))
            {
                // Guard before sending – connection may have dropped while pipeline ran.
                if (!cancellationToken.IsCancellationRequested)
                    await Clients.Caller.SendAsync("MessageError", result.Response, cancellationToken: cancellationToken);
                return;
            }

            // Streaming word-by-word rendering loop.
            // Check cancellationToken on EVERY iteration so the loop stops the instant
            // the client disconnects – prevents stale chunks from being sent.
            var words = (result.Response ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var word in words)
            {
                // Stop immediately if the connection was aborted mid-stream.
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "Streaming loop stopped due to client disconnect | ConnectionId: {ConnectionId}",
                        Context.ConnectionId);
                    return;
                }

                await Clients.Caller.SendAsync("ReceiveChunk", word + " ", cancellationToken: cancellationToken);
                await Task.Delay(30, cancellationToken);
            }

            // Final payload – only send if connection is still alive.
            if (!cancellationToken.IsCancellationRequested)
                await Clients.Caller.SendAsync("MessageComplete", result, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Stream aborted – client disconnected mid-generation. This is expected UX.
            // Log as informational, not error, so monitors are not triggered.
            _logger.LogInformation(
                "Stream aborted | Client disconnected | ConnectionId: {ConnectionId} | Email: {Email}",
                Context.ConnectionId, userEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in SendMessage | ConnectionId: {ConnectionId}", Context.ConnectionId);

            // Attempt to notify client, but only if still connected.
            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Clients.Caller.SendAsync("MessageError", "An unexpected error occurred. Please try again.");
                }
                catch
                {
                    // If send also fails, the connection is gone – silently discard.
                }
            }
        }
    }

    public Task JoinUserGroup(string userEmail)
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, userEmail);
    }
}
