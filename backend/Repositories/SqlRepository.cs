using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace backend.Repositories;

// Dapper-based SQL Server repository implementation.
// All async methods use CommandDefinition so that the CancellationToken is
// passed directly to the underlying SqlCommand – this ensures cooperative
// cancellation: in-flight queries are cancelled cleanly and the connection
// is returned to the pool without being abandoned.
public class SqlRepository : ISqlRepository
{
    private readonly IConfiguration _config;

    public SqlRepository(IConfiguration config)
    {
        _config = config;
    }

    // Open a fresh connection per call – connection pooling handles efficiency
    private IDbConnection CreateConnection()
        => new SqlConnection(_config.GetConnectionString("SqlServer"));

    // Execute a SELECT query and return strongly-typed results.
    // CommandDefinition propagates the CancellationToken to the SQL driver so
    // the database query is cancelled cooperatively when the token fires.
    public async Task<IEnumerable<T>> QueryAsync<T>(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        // Wrap in CommandDefinition so Dapper honours the cancellation token
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        return await conn.QueryAsync<T>(command);
    }

    // Execute a non-query command and return rows affected.
    // CommandDefinition propagates the CancellationToken to the SQL driver so
    // writes (INSERT/UPDATE/DELETE) are cancelled cooperatively.
    public async Task<int> ExecuteAsync(
        string sql,
        object? parameters = null,
        CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();
        // Wrap in CommandDefinition so Dapper honours the cancellation token
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);
        return await conn.ExecuteAsync(command);
    }
}
