using backend.Repositories;

namespace backend.Services;

// Resolves a user by email and manages chat sessions.
// CancellationToken is accepted by every async method and forwarded to the
// repository so that SQL calls cancel cooperatively when the request aborts.
public class SessionService
{
    private readonly ISqlRepository _db;

    public SessionService(ISqlRepository db)
    {
        _db = db;
    }

    // Returns the user's obj_id from the users table; throws if not found.
    // cancellationToken: propagated from HttpContext.RequestAborted so the DB
    // query is cancelled immediately when the client disconnects.
    public async Task<int> GetUserIdAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT obj_id FROM users WHERE e_mail = @Email";
        var result = await _db.QueryAsync<int>(sql, new { Email = email }, cancellationToken);
        var userId = result.FirstOrDefault();

        // Surface a clear error so the caller can return 401/404 gracefully
        if (userId == 0) throw new InvalidOperationException($"User not found: {email}");
        return userId;
    }

    // Returns existing session_id or creates a new one; returns the GUID string.
    // cancellationToken: propagated so both the SELECT check and the INSERT
    // cancel cooperatively if the request is aborted mid-flight.
    public async Task<string> GetOrCreateSessionAsync(
        int userId,
        string? sessionId,
        CancellationToken cancellationToken = default)
    {
        // If no session requested, always create a new one
        if (string.IsNullOrWhiteSpace(sessionId))
            return await CreateSessionAsync(userId, cancellationToken);

        // Verify the session belongs to this user before reusing
        const string checkSql = """
            SELECT SessionId FROM ChatSessions
            WHERE SessionId = @SessionId AND UserId = @UserId AND IsActive = 1
            """;

        var rows = await _db.QueryAsync<string>(
            checkSql,
            new { SessionId = sessionId, UserId = userId },
            cancellationToken);

        return rows.FirstOrDefault() ?? await CreateSessionAsync(userId, cancellationToken);
    }

    // Inserts a new row in chat_sessions and returns the new GUID.
    private async Task<string> CreateSessionAsync(
        int userId,
        CancellationToken cancellationToken = default)
    {
        var newId = Guid.NewGuid().ToString();
        var pkId  = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO ChatSessions (Id, SessionId, UserId, CreatedAt, LastActivityAt, IsActive)
            VALUES (@Id, @SessionId, @UserId, GETUTCDATE(), GETUTCDATE(), 1)
            """;
        await _db.ExecuteAsync(
            insertSql,
            new { Id = pkId, SessionId = newId, UserId = userId },
            cancellationToken);
        return newId;
    }
}
