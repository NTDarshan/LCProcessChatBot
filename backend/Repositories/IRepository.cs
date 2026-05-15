namespace backend.Repositories;

// Generic Dapper repository contract – no Entity Framework.
// CancellationToken is threaded through every async method so that
// SQL queries cancel cooperatively when the HTTP request is aborted.
public interface ISqlRepository
{
    // Execute a SELECT and map rows to type T.
    // Pass cancellationToken so Dapper wraps the command in a CommandDefinition
    // that honours cooperative cancellation (no abandoned queries).
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null, CancellationToken cancellationToken = default);

    // Execute INSERT / UPDATE / DELETE; returns rows affected.
    // Pass cancellationToken so the underlying SqlCommand is cancelled
    // when the caller's token fires.
    Task<int> ExecuteAsync(string sql, object? parameters = null, CancellationToken cancellationToken = default);
}
