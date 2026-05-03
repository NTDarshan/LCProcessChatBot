using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace backend.Repositories;

// Dapper-based SQL Server repository implementation
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

    // Execute a SELECT query and return strongly-typed results
    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null)
    {
        using var conn = CreateConnection();
        return await conn.QueryAsync<T>(sql, parameters);
    }

    // Execute a non-query command and return rows affected
    public async Task<int> ExecuteAsync(string sql, object? parameters = null)
    {
        using var conn = CreateConnection();
        return await conn.ExecuteAsync(sql, parameters);
    }
}
