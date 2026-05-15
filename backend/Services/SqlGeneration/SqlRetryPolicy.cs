namespace backend.Services.SqlGeneration;

/// <summary>
/// Encapsulates retry orchestration metadata for SQL generation.
/// Retry count and attempt labelling are centralised here so that
/// SqlGenerationService contains no magic numbers.
/// </summary>
public sealed class SqlRetryPolicy
{
    /// <summary>Total number of attempts (1 initial + N-1 retries).</summary>
    public int MaxAttempts { get; } = 2;

    /// <summary>Returns a human-readable label for logging purposes.</summary>
    public string GetAttemptLabel(int attemptNumber) =>
        attemptNumber == 1 ? "attempt 1" : $"retry {attemptNumber - 1}";

    /// <summary>
    /// Returns true when the given attempt number falls within the allowed range
    /// and a retry should be executed.
    /// </summary>
    public bool ShouldRetry(int attemptNumber) => attemptNumber < MaxAttempts;
}
