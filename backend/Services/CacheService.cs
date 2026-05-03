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
        return $"chat:{userId}:{Normalise(question)}";

    }
        //=> $"chat:{userId}:{Normalise(question)}";

    public bool TryGetChatResult(int userId, string question, out ChatResponseDto? result)
    {
       
        return _cache.TryGetValue(BuildChatKey(userId, question), out result);
    }
       // => _cache.TryGetValue(BuildChatKey(userId, question), out result);

    public void SetChatResult(int userId, string question, ChatResponseDto result)
    {
        //var options = new MemoryCacheEntryOptions
        //{
        //    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        //    SlidingExpiration = TimeSpan.FromMinutes(1),
        //    Size = 1
        //};

        var options = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(TimeSpan.FromMinutes(5))  // Absolute expiration after 5 minutes means the cache entry will be removed 5 minutes after it's created, regardless of access.
            .SetSlidingExpiration(TimeSpan.FromMinutes(1))   // Sliding expiration of 1 minute means the cache entry will be removed if it hasn't been accessed for 1 minute, even if it's not yet 5 minutes old.
            .SetSize(1);  // Setting the size to 1 allows us to use the cache's size limit features if configured.

        _cache.Set(BuildChatKey(userId, question), result, options);
    }

    public static string BuildSqlKey(int userId, string question)
        => $"sql:{userId}:{Normalise(question)}";

    public bool TryGetSqlResult(int userId, string question, out SqlGenerationResult? result)
        => _cache.TryGetValue(BuildSqlKey(userId, question), out result);

    public void SetSqlResult(int userId, string question, SqlGenerationResult result)
    {
        var options = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10),
            SlidingExpiration = TimeSpan.FromMinutes(5),
            Size = 1
        };

        _cache.Set(BuildSqlKey(userId, question), result, options);
    }
}

