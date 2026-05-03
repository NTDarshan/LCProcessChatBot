using System.Text.RegularExpressions;
using System.Diagnostics;
using backend.Dtos;
using backend.Repositories;
using Microsoft.Extensions.Configuration;

namespace backend.Services;

public class ChatService
{
    private readonly DomainValidationService _domainValidator;
    private readonly AiUnderstandingService _aiUnderstanding;
    private readonly AiResponseService _aiResponse;
    private readonly IntentRouterService _intentRouter;
    private readonly EntityExtractionService _entityExtractor;
    private readonly SessionService _sessionService;
    private readonly ChatHistoryService _historyService;
    private readonly ISqlRepository _db;
    private readonly ILogger<ChatService> _logger;
    private readonly bool _showSqlQuery;
    private readonly SqlGenerationService _sqlGeneration;
    private readonly SqlValidationService _sqlValidation;
    private readonly CacheService _cache;

    public ChatService(DomainValidationService domainValidator,AiUnderstandingService aiUnderstanding,AiResponseService aiResponse,IntentRouterService intentRouter,EntityExtractionService entityExtractor,SessionService sessionService,ChatHistoryService historyService,ISqlRepository db,ILogger<ChatService> logger,IConfiguration config,SqlGenerationService sqlGeneration,SqlValidationService sqlValidation,CacheService cache)
    {
        _domainValidator = domainValidator;
        _aiUnderstanding = aiUnderstanding;
        _aiResponse = aiResponse;
        _intentRouter = intentRouter;
        _entityExtractor = entityExtractor;
        _sessionService = sessionService;
        _historyService = historyService;
        _db = db;
        _logger = logger;
        _showSqlQuery = config.GetValue<bool>("Features:ShowSqlQuery");
        _sqlGeneration = sqlGeneration;
        _sqlValidation = sqlValidation;
        _cache = cache;
    }

    // ── ResponseType map ──────────────────────────────────────────────────────
    private static readonly Dictionary<string, string> ResponseTypeMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["PendingApprovals"] = "approval_list",
            ["PendingApprovalsN1"] = "approval_list",
            ["PendingApprovalsN2"] = "approval_list",
            ["ApprovalBottleneck"] = "approval_list",
            ["MyApprovals"] = "approval_list",
            ["IssuedLC"] = "table",
            ["DraftLC"] = "table",
            ["RejectedLC"] = "table",
            ["CancelledLC"] = "table",
            ["ValidationPending"] = "table",
            ["ExpiringLC"] = "table",
            ["ExpiredLC"] = "table",
            ["OutstandingLC"] = "table",
            ["PaidLC"] = "table",
            ["DelayedRequests"] = "table",
            ["AmendmentRequests"] = "table",
            ["InvoiceStatus"] = "table",
            ["LcStatus"] = "table",
            ["TopBanks"] = "bank_chart",
            ["StatusBreakdown"] = "metric_cards",
            ["CustomerLC"] = "metric_cards",
            ["AmendmentCount"] = "metric_cards",
            ["LcHistory"] = "timeline",
        };

    // Main entry point
    public async Task<ChatResponseDto> ProcessAsync(ChatRequestDto request)
    {
        return await ProcessAsync(request, null);
    }

    public async Task<ChatResponseDto> ProcessAsync(ChatRequestDto request,Func<ProcessingStageUpdateDto, Task>? stageUpdate)
    {
        _logger.LogInformation("Query | Email: {Email} | Message: {Message}",request.UserEmail, request.Message);

        var totalStopwatch = Stopwatch.StartNew();
        var sequence = 0;
        var aiLiveLabels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Task<Dictionary<string, string>>? aiLabelsTask = null;

        static string DefaultLiveLabel(string stageKey)
            => stageKey switch
            {
                "query_reception" => "Receiving your request",
                "query_parse" => "Breaking down your query",
                "context_retrieval" => "Gathering relevant trade context",
                "tool_invocation" => "Setting up data tools",
                "query_building" => "Preparing SQL query",
                "execution_plan" => "Planning data retrieval strategy",
                "db_execution" => "Fetching data from the database",
                "result_processing" => "Organizing the results",
                "response_generation" => "Composing the final answer",
                "final_output" => "Finishing up",
                _ => "Processing request"
            };

        async Task EmitStageAsync(
            string stageKey,
            string stageName,
            int progressPercent,
            string status = "in_progress",
            int? estimatedMsRemaining = null,
            long? elapsedMs = null,
            string? errorMessage = null,
            Dictionary<string, string>? technicalDetails = null)
        {
            if (stageUpdate is null) return;

            sequence++;

            if (aiLabelsTask is not null && aiLiveLabels.Count == 0 && aiLabelsTask.IsCompletedSuccessfully)
            {
                aiLiveLabels = aiLabelsTask.Result;
            }

            var dto = new ProcessingStageUpdateDto
            {
                Sequence = sequence,
                StageKey = stageKey,
                StageName = stageName,
                LiveLabel = aiLiveLabels.TryGetValue(stageKey, out var aiLabel) && !string.IsNullOrWhiteSpace(aiLabel)
                    ? aiLabel
                    : DefaultLiveLabel(stageKey),
                ProgressPercent = progressPercent,
                Status = status,
                EstimatedMsRemaining = estimatedMsRemaining,
                ElapsedMs = elapsedMs,
                ErrorMessage = errorMessage,
                TechnicalDetails = technicalDetails
            };

            try
            {
                await stageUpdate(dto);
            }
            catch (Exception ex)
            {
                // Stage streaming should never break the core pipeline.
                _logger.LogDebug(ex, "Failed to emit processing stage: {StageKey}", stageKey);
            }
        }

        await EmitStageAsync(
            stageKey: "query_reception",
            stageName: "Receiving request",
            progressPercent: 2,
            estimatedMsRemaining: 3800,
            technicalDetails: new Dictionary<string, string>
            {
                ["transport"] = "signalr",
                ["messageLength"] = request.Message.Length.ToString()
            });

        await EmitStageAsync(
            stageKey: "query_parse",
            stageName: "Understanding your question",
            progressPercent: 8,
            estimatedMsRemaining: 3500,
            technicalDetails: new Dictionary<string, string>
            {
                ["questionLength"] = request.Message.Length.ToString(),
                ["containsNumericHints"] = Regex.IsMatch(request.Message, @"\d+").ToString()
            });

        // Step 1 – Domain guard
        if (!_domainValidator.IsLcRelated(request.Message))
        {
            _logger.LogWarning("Domain validation failed: {Message}", request.Message);
            await EmitStageAsync(
                stageKey: "query_parse",
                stageName: "Understanding your question",
                progressPercent: 100,
                status: "failed",
                elapsedMs: totalStopwatch.ElapsedMilliseconds,
                estimatedMsRemaining: 0,
                errorMessage: "Only LC-related queries are supported.",
                technicalDetails: new Dictionary<string, string> { ["domainGuard"] = "failed" });

            return new ChatResponseDto
            {
                Response = "I can only assist with LC-related queries.",
                Data = [],
                SessionId = request.SessionId ?? Guid.NewGuid().ToString(),
                Intent = "Unknown",
                ResponseType = "text_only",
                ResponseLabel = "Out of scope"
            };
        }

        // Step 2 – Resolve user
        var userId = await _sessionService.GetUserIdAsync(request.UserEmail);

        // Step 3 – Session
        var sessionId = await _sessionService.GetOrCreateSessionAsync(userId, request.SessionId);

        // Step 4 – Save user message
        await _historyService.SaveMessageAsync(sessionId, "user", request.Message);

        // Step 5 – Entity extraction
        var entities = _entityExtractor.Extract(request.Message);

        // Step 6 – Intent classification (used for ResponseLabel only — not for routing)
        var intent = await _aiUnderstanding.GetIntentAsync(request.Message);
        _logger.LogInformation("Intent (label only): {Intent} | Session: {Session}", intent, sessionId);

        aiLabelsTask = _aiUnderstanding.GenerateProcessingLabelsAsync(request.Message, intent);
        await EmitStageAsync(
            stageKey: "query_parse",
            stageName: "Understanding your question",
            progressPercent: 24,
            status: "completed",
            estimatedMsRemaining: 3000,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["intentLabel"] = intent,
                ["sessionId"] = sessionId,
                ["entityLcNumber"] = entities.LcNumber ?? "null",
                ["entityBankName"] = entities.BankName ?? "null",
                ["entityStatus"] = entities.Status ?? "null"
            });

        await EmitStageAsync(
            stageKey: "context_retrieval",
            stageName: "Gathering trade context",
            progressPercent: 34,
            estimatedMsRemaining: 2500,
            technicalDetails: new Dictionary<string, string>
            {
                ["userId"] = userId.ToString(),
                ["sessionResolved"] = "true"
            });

        if (_cache.TryGetChatResult(userId, request.Message, out var cached) && cached is not null)
        {
            _logger.LogInformation("Cache HIT | UserId: {UserId}", userId);

            var cachedResponse = new ChatResponseDto
            {
                Response = cached.Response,
                Data = cached.Data,
                SessionId = sessionId,
                ExecutedQuery = cached.ExecutedQuery,
                Intent = cached.Intent,
                ResponseType = cached.ResponseType,
                ResponseLabel = cached.ResponseLabel,
                ChartType = cached.ChartType,
                IsTextToSql = cached.IsTextToSql,
                QueryType = cached.QueryType
            };

            var serializedCachedData = System.Text.Json.JsonSerializer.Serialize(cachedResponse.Data);
            await _historyService.SaveMessageAsync(
                sessionId, "assistant", cachedResponse.Response,
                cachedResponse.ExecutedQuery,
                cachedResponse.Intent, cachedResponse.ResponseType, serializedCachedData, cachedResponse.QueryType);

            await EmitStageAsync(
                stageKey: "cache_hit",
                stageName: "Found quick answer",
                progressPercent: 100,
                status: "completed",
                estimatedMsRemaining: 0,
                elapsedMs: totalStopwatch.ElapsedMilliseconds,
                technicalDetails: new Dictionary<string, string>
                {
                    ["cacheType"] = "chat",
                    ["responseType"] = cachedResponse.ResponseType ?? "unknown"
                });

            return cachedResponse;
        }

        await EmitStageAsync(
            stageKey: "context_retrieval",
            stageName: "Gathering trade context",
            progressPercent: 46,
            status: "completed",
            estimatedMsRemaining: 2100,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["cacheHit"] = "false",
                ["preprocessing"] = "entity extraction and intent labeling complete"
            });

        await EmitStageAsync(
            stageKey: "tool_invocation",
            stageName: "Preparing tools",
            progressPercent: 52,
            estimatedMsRemaining: 2000,
            technicalDetails: new Dictionary<string, string>
            {
                ["tool"] = "SqlGenerationService",
                ["mode"] = "text_to_sql"
            });

        _logger.LogInformation(
            "TEXT-TO-SQL | Session: {Session} | Message: {Message}",
            sessionId, request.Message);

        await EmitStageAsync(
            stageKey: "query_building",
            stageName: "Preparing SQL query",
            progressPercent: 56,
            estimatedMsRemaining: 1900,
            technicalDetails: new Dictionary<string, string>
            {
                ["pipeline"] = "text_to_sql",
                ["parameterBinding.userId"] = userId.ToString(),
                ["parameterBinding.lcNumber"] = entities.LcNumber ?? "null",
                ["parameterBinding.bankName"] = entities.BankName ?? "null",
                ["parameterBinding.customerName"] = entities.CustomerName ?? "null",
                ["parameterBinding.status"] = entities.Status ?? "null"
            });

        var genResult = await _sqlGeneration.GenerateSqlAsync(request.Message, userId);

        List<dynamic> dbRows;
        string responseType;
        string responseLabel;

        if (!genResult.IsSuccess)
        {
            _logger.LogWarning(
                "SQL generation failed: {Error} | Message: {Message}",
                genResult.Error, request.Message);
            await EmitStageAsync(
                stageKey: "query_building",
                stageName: "Preparing SQL query",
                progressPercent: 100,
                status: "failed",
                elapsedMs: totalStopwatch.ElapsedMilliseconds,
                estimatedMsRemaining: 0,
                errorMessage: genResult.Error,
                technicalDetails: new Dictionary<string, string> { ["sqlGeneration"] = "failed" });

            return new ChatResponseDto
            {
                Response      = $"I wasn't able to generate a safe SQL query for that question. {genResult.Error}. Please try rephrasing.",
                Data          = [],
                SessionId     = sessionId,
                Intent        = intent,
                ResponseType  = "text_only",
                ResponseLabel = "Query generation failed",
                IsTextToSql   = true
            };
        }

        await EmitStageAsync(
            stageKey: "tool_invocation",
            stageName: "Preparing tools",
            progressPercent: 60,
            status: "completed",
            estimatedMsRemaining: 1600,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["tool"] = "SqlGenerationService",
                ["status"] = "success"
            });

        _logger.LogInformation(
            "SQL ready | ResponseType: {RT} | Reasoning: {R}",
            genResult.ResponseType, genResult.Reasoning);
        await EmitStageAsync(
            stageKey: "query_building",
            stageName: "Preparing SQL query",
            progressPercent: 66,
            status: "completed",
            estimatedMsRemaining: 1400,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["queryType"] = genResult.QueryType ?? "unknown",
                ["responseType"] = genResult.ResponseType ?? "unknown",
                ["sqlLength"] = genResult.Sql.Length.ToString()
            });

        await EmitStageAsync(
            stageKey: "execution_plan",
            stageName: "Selecting data retrieval strategy",
            progressPercent: 72,
            status: "completed",
            estimatedMsRemaining: 1100,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["queryType"] = genResult.QueryType ?? "unknown",
                ["renderer"] = genResult.ResponseType ?? "table",
                ["reasoning"] = string.IsNullOrWhiteSpace(genResult.Reasoning) ? "n/a" : genResult.Reasoning
            });

        _cache.SetSqlResult(userId, request.Message, genResult);

        try
        {
            var sqlStopwatch = Stopwatch.StartNew();
            await EmitStageAsync(
                stageKey: "db_execution",
                stageName: "Fetching data from database",
                progressPercent: 80,
                estimatedMsRemaining: 700,
                technicalDetails: new Dictionary<string, string>
                {
                    ["command"] = "QueryAsync<dynamic>",
                    ["sqlPreview"] = genResult.Sql.Length > 220
                        ? $"{genResult.Sql[..220]}..."
                        : genResult.Sql,
                    ["boundParameters"] = $"UserId={userId}"
                });

            dbRows = (await _db.QueryAsync<dynamic>(genResult.Sql, new { UserId = userId })).ToList();
            _logger.LogInformation("SQL returned {Count} rows", dbRows.Count);
            sqlStopwatch.Stop();

            await EmitStageAsync(
                stageKey: "db_execution",
                stageName: "Fetching data from database",
                progressPercent: 86,
                status: "completed",
                estimatedMsRemaining: 500,
                elapsedMs: sqlStopwatch.ElapsedMilliseconds,
                technicalDetails: new Dictionary<string, string>
                {
                    ["rowsReturned"] = dbRows.Count.ToString(),
                    ["executionMs"] = sqlStopwatch.ElapsedMilliseconds.ToString()
                });
        }
        catch (Exception sqlEx)
        {
            _logger.LogError(sqlEx, "SQL execution failed | SQL: {Sql}", genResult.Sql);
            await EmitStageAsync(
                stageKey: "db_execution",
                stageName: "Fetching data from database",
                progressPercent: 100,
                status: "failed",
                elapsedMs: totalStopwatch.ElapsedMilliseconds,
                estimatedMsRemaining: 0,
                errorMessage: sqlEx.Message,
                technicalDetails: new Dictionary<string, string>
                {
                    ["sqlPreview"] = genResult.Sql.Length > 220
                        ? $"{genResult.Sql[..220]}..."
                        : genResult.Sql
                });

            return new ChatResponseDto
            {
                Response      = "The generated query encountered a database error. Please try rephrasing your question.",
                Data          = [],
                SessionId     = sessionId,
                Intent        = intent,
                ResponseType  = "text_only",
                ResponseLabel = "Execution error",
                IsTextToSql   = true,
                ExecutedQuery = _showSqlQuery ? genResult.Sql : null
            };
        }

        responseType  = genResult.ResponseType;
        responseLabel = $"Results · {dbRows.Count} record{(dbRows.Count != 1 ? "s" : "")}";
        await EmitStageAsync(
            stageKey: "result_processing",
            stageName: "Organizing results",
            progressPercent: 92,
            status: "completed",
            estimatedMsRemaining: 300,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["targetResponseType"] = responseType,
                ["queryType"] = genResult.QueryType ?? "unknown",
                ["rowCount"] = dbRows.Count.ToString()
            });

        // ── Step 8 — AI natural language summary ────────────────────────────────
        string aiResponse;
        if (dbRows.Count == 0)
        {
            aiResponse = "No records found";
        }
        else
        {
            aiResponse = await _aiResponse.GenerateResponseAsync(request.Message, dbRows);
            var lower = aiResponse.Trim().ToLowerInvariant();
            if (lower.StartsWith("no records") || lower.StartsWith("no data"))
                aiResponse = $"Found {dbRows.Count} record(s) matching your query.";
        }

        await EmitStageAsync(
            stageKey: "response_generation",
            stageName: "Composing final answer",
            progressPercent: 98,
            status: "completed",
            estimatedMsRemaining: 100,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["usedAiSummarizer"] = (dbRows.Count > 0).ToString(),
                ["finalResponseLength"] = aiResponse.Length.ToString()
            });

        // ── Step 9 — Persist and return ──────────────────────────────────────────
        var serializedData = System.Text.Json.JsonSerializer.Serialize(dbRows);
        await _historyService.SaveMessageAsync(
            sessionId, "assistant", aiResponse,
            _showSqlQuery ? genResult.Sql : null,
            intent, responseType, serializedData, genResult.QueryType);

        _logger.LogInformation(
            "Done | Session: {Session} | Rows: {Count} | RT: {RT}",
            sessionId, dbRows.Count, responseType);

        var finalResponse = new ChatResponseDto
        {
            Response      = aiResponse,
            Data          = dbRows,
            SessionId     = sessionId,
            ExecutedQuery = _showSqlQuery ? genResult.Sql : null,
            Intent        = intent,
            ResponseType  = responseType,
            ResponseLabel = responseLabel,
            IsTextToSql   = true,
            QueryType     = genResult.QueryType
        };

        if (dbRows.Count > 0
            && !string.Equals(finalResponse.ResponseType, "text_only", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(finalResponse.ResponseType, "approval_list", StringComparison.OrdinalIgnoreCase))
        {
            _cache.SetChatResult(userId, request.Message, finalResponse);
        }

        await EmitStageAsync(
            stageKey: "final_output",
            stageName: "Ready",
            progressPercent: 100,
            status: "completed",
            estimatedMsRemaining: 0,
            elapsedMs: totalStopwatch.ElapsedMilliseconds,
            technicalDetails: new Dictionary<string, string>
            {
                ["sessionId"] = sessionId,
                ["totalElapsedMs"] = totalStopwatch.ElapsedMilliseconds.ToString(),
                ["responseType"] = finalResponse.ResponseType ?? "unknown"
            });

        return finalResponse;
    }

    // ── BuildResponseLabel ────────────────────────────────────────────────────
    private static string BuildResponseLabel(string intent, int count)
    {
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PendingApprovals"]   = "Pending approvals",
            ["PendingApprovalsN1"] = "Pending N+1",
            ["PendingApprovalsN2"] = "Pending N+2",
            ["ApprovalBottleneck"] = "Approval bottlenecks",
            ["MyApprovals"]        = "My approvals",
            ["IssuedLC"]           = "Issued LCs",
            ["DraftLC"]            = "Draft requests",
            ["RejectedLC"]         = "Rejected LCs",
            ["CancelledLC"]        = "Cancelled LCs",
            ["ValidationPending"]  = "Validation pending",
            ["ExpiringLC"]         = "Expiring soon",
            ["ExpiredLC"]          = "Expired LCs",
            ["OutstandingLC"]      = "Outstanding LCs",
            ["PaidLC"]             = "Paid LCs",
            ["DelayedRequests"]    = "Delayed requests",
            ["AmendmentRequests"]  = "Amendment history",
            ["AmendmentCount"]     = "Amendment counts",
            ["InvoiceStatus"]      = "Invoice status",
            ["LcStatus"]           = "LC details",
            ["TopBanks"]           = "Banks by value",
            ["StatusBreakdown"]    = "LC overview",
            ["CustomerLC"]         = "LCs by customer",
            ["LcHistory"]          = "Activity timeline",
        };
        var label = labels.TryGetValue(intent, out var l) ? l : "Results";
        return $"{label} · {count} record{(count != 1 ? "s" : "")}";
    }

    // ── BuildResolvedQuery ────────────────────────────────────────────────────
    // Display-only query with param values inlined. Never used for DB access.
    private static string BuildResolvedQuery(
        string sqlTemplate,
        QueryEntitiesDto entities,
        int userId)
    {
        var sql = sqlTemplate.Trim();

        static string Esc(string s) => s.Replace("'", "''");
        static string Str(string? s) => s is null ? "CAST(NULL AS NVARCHAR(MAX))" : $"'{Esc(s)}'";
        static string Num(object? v) =>
            v switch
            {
                decimal d => d.ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                int i => i.ToString(),
                _ => "CAST(NULL AS INT)"
            };
        static string Dt(DateTime? d) =>
            d is null ? "CAST(NULL AS DATE)" : $"'{d.Value:yyyy-MM-dd}'";

        sql = Regex.Replace(sql, @"@LcNumber\b", Str(entities.LcNumber), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@MinAmount\b", Num(entities.MinAmount), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@MaxAmount\b", Num(entities.MaxAmount), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@DaysRange\b", Num(entities.DaysRange), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@BankName\b", Str(entities.BankName), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@CustomerName\b", Str(entities.CustomerName), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@Status\b", Str(entities.Status), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@IsPendingStatus\b", entities.IsPendingStatus ? "1" : "0", RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@StartDate\b", Dt(entities.StartDate), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@EndDate\b", Dt(entities.EndDate), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@Country\b", Str(entities.Country), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@CurrencyCode\b", Str(entities.CurrencyCode), RegexOptions.IgnoreCase);
        sql = Regex.Replace(sql, @"@UserId\b", userId.ToString(), RegexOptions.IgnoreCase);

        return sql;
    }
}

