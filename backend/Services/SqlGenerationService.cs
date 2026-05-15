using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using backend.Services.SqlGeneration;

namespace backend.Services;

/// <summary>
/// Orchestrates AI-powered SQL generation.
/// Responsibilities: cache check → prompt build → OpenAI call → parse/validate → retry → cache store.
/// All prompt content, parsing, validation and retry metadata are delegated to the
/// SqlGeneration sub-providers for single-responsibility compliance.
///
/// CANCELLATION ENHANCEMENT:
/// CancellationToken is threaded through GenerateSqlAsync and all internal calls
/// so that Azure OpenAI completions are cancelled immediately when the request
/// is aborted, preventing wasted token spend on discarded SQL generation.
/// </summary>
public class SqlGenerationService
{
    private readonly ChatClient _chatClient;
    private readonly SqlPromptBuilder _promptBuilder;
    private readonly SqlResponseParser _parser;
    private readonly SqlRetryPolicy _retryPolicy;
    private readonly ILogger<SqlGenerationService> _logger;
    private readonly CacheService _cache;

    // ─── CONSTRUCTOR ─────────────────────────────────────────────────────────
    public SqlGenerationService(
        IConfiguration config,
        SqlValidationService validator,
        ILogger<SqlGenerationService> logger,
        ILogger<SqlResponseParser> parserLogger,
        CacheService cache,
        SchemaProvider schema,
        QueryRuleProvider rules,
        ResponseTypeGuideProvider guide)
    {
        _logger      = logger;
        _cache       = cache;
        _retryPolicy = new SqlRetryPolicy();
        _parser      = new SqlResponseParser(validator, parserLogger);
        _promptBuilder = new SqlPromptBuilder(schema, rules, guide);

        var endpoint   = config["OpenAI:Endpoint"]!;
        var key        = config["OpenAI:Key"]!;
        var deployment = config["OpenAI:Deployment"]!;
        var client     = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        _chatClient    = client.GetChatClient(deployment);
    }

    // ─── PUBLIC ENTRY POINT ───────────────────────────────────────────────────
    // cancellationToken: propagated from HttpContext.RequestAborted so that
    // Azure OpenAI calls stop immediately when the client disconnects.
    public async Task<SqlGenerationResult> GenerateSqlAsync(
        string userQuestion,
        int userId,
        CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetSqlResult(userId, userQuestion, out var cached) && cached is not null)
        {
            _logger.LogInformation("SQL cache HIT | UserId: {UserId}", userId);
            return cached;
        }

        // Attempt 1 – pass the cancellation token so OpenAI call stops on abort
        var result = await GenerateInternalAsync(userQuestion, userId, retryError: null, attempt: 1, cancellationToken);

        // Single auto-retry on failure (only if not cancelled)
        if (!result.IsSuccess && _retryPolicy.ShouldRetry(1))
        {
            _logger.LogWarning(
                "Attempt 1 failed (type={Type}) — auto-retrying: {Error}",
                result.IsValidationError ? "validation" : "parse/other",
                result.Error);
            result = await GenerateInternalAsync(userQuestion, userId, retryError: result.Error, attempt: 2, cancellationToken);
        }

        if (result.IsSuccess)
        {
            _cache.SetSqlResult(userId, userQuestion, result);
        }

        return result;
    }

    // ─── INTERNAL GENERATION ─────────────────────────────────────────────────
    // cancellationToken: forwarded to CompleteChatAsync so the Azure OpenAI HTTP
    // call is cancelled when the upstream token fires – preventing token spend
    // on completions that will be discarded.
    private async Task<SqlGenerationResult> GenerateInternalAsync(
        string userQuestion,
        int userId,
        string? retryError,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = _promptBuilder.BuildSystemPrompt();
        var userPrompt   = _promptBuilder.BuildUserPrompt(userQuestion, userId, retryError);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        try
        {
            // Pass cancellationToken: if the request is aborted, this call stops
            // immediately rather than completing a (now useless) SQL generation.
            var completion = await _chatClient.CompleteChatAsync(messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 3000,  // raised from 2000 — complex queries need more headroom
                    Temperature         = 0.0f   // deterministic — SQL must not vary between calls
                },
                cancellationToken);

            var raw = completion.Value.Content[0].Text.Trim();
            _logger.LogInformation(
                "SqlGeneration raw ({Attempt}):\n{Output}",
                _retryPolicy.GetAttemptLabel(attempt),
                raw.Length > 1200 ? raw[..1200] + "…[truncated for log]" : raw);

            return _parser.ParseAndValidate(raw);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected behaviour – log informational, not error.
            _logger.LogInformation("SQL generation cancelled by client disconnect (attempt {Attempt})", attempt);
            throw; // Re-throw so the pipeline unwinds cleanly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SqlGenerationService call failed");
            return SqlGenerationResult.Fail(
                "AI service call failed: " + ex.Message,
                isValidationError: false);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SqlGenerationResult
//  Added: IsValidationError (bool) and QueryType (string?) vs previous version
//  ChatService.cs must handle the new Fail() signature: Fail(error, isValidationError)
// ─────────────────────────────────────────────────────────────────────────────
public record SqlGenerationResult(
    bool IsSuccess,
    string Sql,
    string ResponseType,
    string? ChartType,
    string? QueryType,
    string? Reasoning,
    string? Error,
    bool IsValidationError)
{
    public static SqlGenerationResult Ok(
        string sql, string responseType, string? chartType,
        string queryType, string reasoning)
        => new(true, sql, responseType, chartType, queryType, reasoning, null, false);

    public static SqlGenerationResult Fail(string error, bool isValidationError)
        => new(false, string.Empty, "table", null, null, null, error, isValidationError);
}
