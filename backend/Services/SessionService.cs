using backend.Repositories;

namespace backend.Services;

// Resolves a user by email and manages chat sessions
public class SessionService
{
    private readonly ISqlRepository _db;

    public SessionService(ISqlRepository db)
    {
        _db = db;
    }

    // Returns the user's obj_id from the users table; throws if not found
    public async Task<int> GetUserIdAsync(string email)
    {
        const string sql = "SELECT obj_id FROM users WHERE e_mail = @Email";
        var result = await _db.QueryAsync<int>(sql, new { Email = email });
        var userId = result.FirstOrDefault();

        // Surface a clear error so the caller can return 401/404 gracefully
        if (userId == 0) throw new InvalidOperationException($"User not found: {email}");
        return userId;
    }

    // Returns existing session_id or creates a new one; returns the GUID string
    public async Task<string> GetOrCreateSessionAsync(int userId, string? sessionId)
    {
        // If no session requested, always create a new one
        if (string.IsNullOrWhiteSpace(sessionId))
            return await CreateSessionAsync(userId);

        // Verify the session belongs to this user before reusing
        const string checkSql = """
            SELECT SessionId FROM ChatSessions
            WHERE SessionId = @SessionId AND UserId = @UserId AND IsActive = 1
            """;

        var rows = await _db.QueryAsync<string>(checkSql, new { SessionId = sessionId, UserId = userId });
        return rows.FirstOrDefault() ?? await CreateSessionAsync(userId);
    }

    // Inserts a new row in chat_sessions and returns the new GUID
    private async Task<string> CreateSessionAsync(int userId)
    {
        var newId = Guid.NewGuid().ToString();
        var pkId = Guid.NewGuid();
        const string insertSql = """
            INSERT INTO ChatSessions (Id, SessionId, UserId, CreatedAt, LastActivityAt, IsActive)
            VALUES (@Id, @SessionId, @UserId, GETUTCDATE(), GETUTCDATE(), 1)
            """;
        await _db.ExecuteAsync(insertSql, new { Id = pkId, SessionId = newId, UserId = userId });
        return newId;
    }
}
