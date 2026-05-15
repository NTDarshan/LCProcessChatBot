using System.Text.RegularExpressions;
using backend.Dtos;
using Microsoft.Extensions.Caching.Memory;

namespace backend.Services;

public class CacheService
{
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);

    private readonly IMemoryCache _cache;

    public CacheService(IMemoryCache cache)
    {
        _cache = cache;
    }

    private static string Normalise(string question)
    {
        var trimmed = (question ?? string.Empty).Trim().ToLowerInvariant();
        return MultiSpace.Replace(trimmed, " ");
    }

    public static string BuildChatKey(int userId, string question)
    {
        return $"chat:global:{Normalise(question)}";
    }

    public bool TryGetChatResult(int userId, string question, out ChatResponseDto? result)
    {
       
        return _cache.TryGetValue(BuildChatKey(userId, question), out result);
    }

    public void SetChatResult(int userId, string question, ChatResponseDto result)
    {
        var options = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(3))
            .SetSlidingExpiration(TimeSpan.FromMinutes(1))
            .SetSize(1);

        _cache.Set(BuildChatKey(userId, question), result, options);
    }

    public static string BuildSqlKey(int userId, string question)
    {
        return $"sql:global:{Normalise(question)}";
    }
    public bool TryGetSqlResult(int userId, string question, out SqlGenerationResult? result)
    {
        return _cache.TryGetValue(BuildSqlKey(userId, question), out result);
    }

    public void SetSqlResult(int userId, string question, SqlGenerationResult result)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
            SlidingExpiration = TimeSpan.FromMinutes(2),
            Size = 1
        };

        _cache.Set(BuildSqlKey(userId, question), result, options);
    }

    public void InvalidateQuestion(string question)
    {
        _cache.Remove(BuildChatKey(0, question));
        _cache.Remove(BuildSqlKey(0, question));
    }
}

