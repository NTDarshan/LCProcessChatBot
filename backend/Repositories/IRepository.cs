namespace backend.Repositories;

// Generic Dapper repository contract – no Entity Framework
public interface ISqlRepository
{
    // Execute a SELECT and map rows to type T
    Task<IEnumerable<T>> QueryAsync<T>(string sql, object? parameters = null);

    // Execute INSERT / UPDATE / DELETE; returns rows affected
    Task<int> ExecuteAsync(string sql, object? parameters = null);
}
