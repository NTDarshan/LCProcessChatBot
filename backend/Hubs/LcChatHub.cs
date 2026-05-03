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
        _logger = logger;
        _chatService = chatService;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("SignalR connected | ConnectionId: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation(
            "SignalR disconnected | ConnectionId: {ConnectionId} | Exception: {ExceptionMessage}",
            Context.ConnectionId,
            exception?.Message);

        return base.OnDisconnectedAsync(exception);
    }

    public async Task SendMessage(string userEmail, string message, string? sessionId)
    {
        var request = new ChatRequestDto
        {
            UserEmail = userEmail,
            Message = message,
            SessionId = sessionId
        };

        await Clients.Caller.SendAsync("MessageReceived");

        async Task EmitStageAsync(ProcessingStageUpdateDto stage)
        {
            try
            {
                await Clients.Caller.SendAsync("ProcessingStage", stage);
            }
            catch (Exception ex)
            {
                // Keep pipeline resilient even if the socket drops mid-stream.
                _logger.LogDebug(ex, "Failed to stream processing stage for {ConnectionId}", Context.ConnectionId);
            }
        }

        var result = await _chatService.ProcessAsync(request, EmitStageAsync);

        if (string.Equals(result.ResponseType, "text_only", StringComparison.OrdinalIgnoreCase))
        {
            await Clients.Caller.SendAsync("MessageError", result.Response);
            return;
        }

        var words = (result.Response ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            await Clients.Caller.SendAsync("ReceiveChunk", word + " ");
            await Task.Delay(30);
        }

        await Clients.Caller.SendAsync("MessageComplete", result);
    }

    public Task JoinUserGroup(string userEmail)
        => Groups.AddToGroupAsync(Context.ConnectionId, userEmail);
}

