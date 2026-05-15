using backend.Dtos;
using backend.Repositories;

namespace backend.Services;

// Persists and retrieves chat messages and sessions from the database.
// CancellationToken is accepted by every async method and forwarded to the
// repository so that SQL calls cancel cooperatively when the request aborts.
public class ChatHistoryService
{
    private readonly ISqlRepository _db;

    public ChatHistoryService(ISqlRepository db)
    {
        _db = db;
    }

    // Saves a single message (user or assistant) with a UTC timestamp.
    // executedQuery is only set on assistant messages; null is stored for user messages.
    // cancellationToken: forwarded to the repository so the INSERT and UPDATE
    // cancel cooperatively if the request is aborted.
    public async Task SaveMessageAsync(
        string sessionId,
        string role,
        string content,
        string? executedQuery = null,
        string? intent = null,
        string? responseType = null,
        string? data = null,
        string? queryType = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            INSERT INTO ChatMessages (SessionId, Role, Message, ExecutedQuery, Intent, ResponseType, Data, QueryType, CreatedAt)
            VALUES (@SessionId, @Role, @Message, @ExecutedQuery, @Intent, @ResponseType, @Data, @QueryType, GETUTCDATE());
            
            UPDATE ChatSessions 
            SET LastActivityAt = GETUTCDATE() 
            WHERE SessionId = @SessionId;
            """;

        await _db.ExecuteAsync(sql, new
        {
            SessionId     = sessionId,
            Role          = role,
            Message       = content,
            ExecutedQuery = executedQuery,
            Intent        = intent,
            ResponseType  = responseType,
            Data          = data,
            QueryType     = queryType
        }, cancellationToken);
    }

    // Returns all sessions for a user ordered newest-first; title is the first user message.
    // cancellationToken: forwarded to the repository so the SELECT cancels if the
    // client disconnects before the query completes.
    public async Task<IEnumerable<ChatSessionDto>> GetSessionsAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                cs.SessionId                                                AS SessionId,
                cs.CreatedAt                                                AS CreatedAt,
                -- Use the first user message as the session title if Title is null
                ISNULL(cs.Title, (
                    SELECT TOP 1 ch.Message
                    FROM   ChatMessages ch
                    WHERE  ch.SessionId = cs.SessionId
                    AND    ch.Role      = 'user'
                    ORDER  BY ch.CreatedAt ASC
                ))                                                          AS Title
            FROM ChatSessions cs
            WHERE cs.UserId = @UserId AND cs.IsActive = 1
            ORDER BY cs.LastActivityAt DESC
            """;

        return await _db.QueryAsync<ChatSessionDto>(sql, new { UserId = userId }, cancellationToken);
    }

    // Returns all messages in a session ordered chronologically for chat replay.
    // Also returns ExecutedQuery so the frontend can show "View Query" on history load.
    // cancellationToken: forwarded so the SELECT cancels cooperatively.
    public async Task<IEnumerable<ChatHistoryItemDto>> GetSessionHistoryAsync(
        string sessionId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT
                ch.Role          AS Role,
                ch.Message       AS Content,
                ch.ExecutedQuery AS ExecutedQuery,
                ch.Intent        AS Intent,
                ch.ResponseType  AS ResponseType,
                ch.QueryType     AS QueryType,
                ch.Data          AS Data,
                ch.CreatedAt     AS CreatedAt
            FROM ChatMessages ch
            -- Join back to ChatSessions so we can verify the session belongs to this user
            JOIN ChatSessions cs ON cs.SessionId = ch.SessionId
            WHERE ch.SessionId = @SessionId
              AND cs.UserId    = @UserId
              AND cs.IsActive  = 1
            ORDER BY ch.CreatedAt ASC
            """;

        return await _db.QueryAsync<ChatHistoryItemDto>(
            sql,
            new { SessionId = sessionId, UserId = userId },
            cancellationToken);
    }

    // Soft-deletes a session and cascades to its messages.
    // Using soft delete (IsActive = 0) so history is recoverable if needed.
    // cancellationToken: forwarded so the UPDATE cancels cooperatively.
    public async Task DeleteSessionAsync(
        string sessionId,
        int userId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE ChatSessions
            SET IsActive = 0, LastActivityAt = GETUTCDATE()
            WHERE SessionId = @SessionId AND UserId = @UserId;
            """;

        await _db.ExecuteAsync(sql, new { SessionId = sessionId, UserId = userId }, cancellationToken);
    }
}
