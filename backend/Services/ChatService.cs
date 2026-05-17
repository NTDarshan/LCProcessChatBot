using System.Text.RegularExpressions;
using System.Diagnostics;
using backend.Dtos;
using backend.Models;
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
    private readonly SuggestedQuestionsService _suggestedQuestionsService;
    private readonly ClarificationService _clarificationService;

    public ChatService(DomainValidationService domainValidator,AiUnderstandingService aiUnderstanding,AiResponseService aiResponse,IntentRouterService intentRouter,EntityExtractionService entityExtractor,SessionService sessionService,ChatHistoryService historyService,ISqlRepository db,ILogger<ChatService> logger,IConfiguration config,SqlGenerationService sqlGeneration,SqlValidationService sqlValidation,CacheService cache,SuggestedQuestionsService suggestedQuestionsService,ClarificationService clarificationService)
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
        _suggestedQuestionsService = suggestedQuestionsService;
        _clarificationService = clarificationService;
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

    // Main entry point – no cancellation token (HTTP fallback path)
    public async Task<ChatResponseDto> ProcessAsync(ChatRequestDto request)
    {
        return await ProcessAsync(request, null, null, CancellationToken.None);
    }

    // Overload with stage updates only (used internally / SignalR path without token)
    public async Task<ChatResponseDto> ProcessAsync(ChatRequestDto request, Func<ProcessingStageUpdateDto, Task>? stageUpdate)
    {
        return await ProcessAsync(request, stageUpdate, null, CancellationToken.None);
    }

    // Overload with cancellation token only (HTTP controller path)
    public async Task<ChatResponseDto> ProcessAsync(ChatRequestDto request, CancellationToken cancellationToken)
    {
        return await ProcessAsync(request, null, null, cancellationToken);
    }

    // Overload with stage updates and token streaming
    public async Task<ChatResponseDto> ProcessAsync(
        ChatRequestDto request,
        Func<ProcessingStageUpdateDto, Task>? stageUpdate,
        Func<string, Task>? onTokenReceived)
    {
        return await ProcessAsync(request, stageUpdate, onTokenReceived, CancellationToken.None);
    }

    // Primary overload – accepts stage callback, token streaming callback, and cancellation token.
    // cancellationToken: should be HttpContext.RequestAborted so the entire
    // pipeline unwinds cooperatively when the client disconnects.
    public async Task<ChatResponseDto> ProcessAsync(
        ChatRequestDto request,
        Func<ProcessingStageUpdateDto, Task>? stageUpdate,
        Func<string, Task>? onTokenReceived,
        CancellationToken cancellationToken)
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
            // Skip emission entirely once cancellation is requested – the client
            // is gone and further SignalR pushes would be orphaned.
            if (stageUpdate is null || cancellationToken.IsCancellationRequested) return;

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
                Response = "I'm here to help with LC process questions — like your pending approvals, issued or expiring LCs, amendment history, invoice status, payment tracking, and other trade finance activities. What would you like to know about your letters of credit?",
                Data = [],
                SessionId = request.SessionId ?? Guid.NewGuid().ToString(),
                Intent = "Unknown",
                ResponseType = "text_only",
                ResponseLabel = "Out of scope"
            };
        }

        // Step 2 – Resolve user (propagate cancellationToken to SQL)
        var userId = await _sessionService.GetUserIdAsync(request.UserEmail, cancellationToken);

        // Step 3 – Session (propagate cancellationToken to SQL)
        var sessionId = await _sessionService.GetOrCreateSessionAsync(userId, request.SessionId, cancellationToken);

        // Step 4 – Save user message (propagate cancellationToken to SQL)
        await _historyService.SaveMessageAsync(sessionId, "user", request.Message, cancellationToken: cancellationToken);

        // Step 5 – Entity extraction
        var entities = _entityExtractor.Extract(request.Message);

        // Step 6 – Intent classification (used for ResponseLabel only — not for routing)
        // Pass cancellationToken so the Azure OpenAI call stops if the client disconnects.
        var intent = await _aiUnderstanding.GetIntentAsync(request.Message, cancellationToken);
        _logger.LogInformation("Intent (label only): {Intent} | Session: {Session}", intent, sessionId);

        // Fire-and-forget label generation – pass token so it cancels if pipeline aborts early
        aiLabelsTask = _aiUnderstanding.GenerateProcessingLabelsAsync(request.Message, intent, cancellationToken);
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
                cachedResponse.Intent, cachedResponse.ResponseType, serializedCachedData, cachedResponse.QueryType,
                cancellationToken);

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

        // ── Routing Decision ──────────────────────────────────────────────────
        var routing = DetermineRouting(intent);
        _logger.LogInformation(
            "Routing | UseIntentRouter: {Use} | Reason: {Reason}",
            routing.UseIntentRouter, routing.Reason);

        List<dynamic> dbRows;
        string responseType;
        string responseLabel;
        string? executedSql;
        string? queryType;
        bool isTextToSql;

        if (routing.UseIntentRouter)
        {
            // ── Known Intent Route ────────────────────────────────────────────
            // The intent is registered in IntentRouterService; use predefined SQL.
            // SqlGenerationService is NOT invoked on this path.
            await EmitStageAsync(
                stageKey: "tool_invocation",
                stageName: "Preparing tools",
                progressPercent: 52,
                estimatedMsRemaining: 2000,
                technicalDetails: new Dictionary<string, string>
                {
                    ["tool"] = "IntentRouterService",
                    ["mode"] = "predefined_sql",
                    ["intent"] = intent
                });

            var definition = _intentRouter.GetDefinition(intent);
            if (definition is null)
            {
                // Defensive: HasIntent returned true but GetDefinition returned null — should never happen.
                _logger.LogError("IntentRouterService.GetDefinition returned null for a registered intent: {Intent}", intent);
                await EmitStageAsync(
                    stageKey: "tool_invocation",
                    stageName: "Preparing tools",
                    progressPercent: 100,
                    status: "failed",
                    elapsedMs: totalStopwatch.ElapsedMilliseconds,
                    estimatedMsRemaining: 0,
                    errorMessage: "Internal routing error.",
                    technicalDetails: new Dictionary<string, string> { ["intentRouter"] = "definition_missing" });

                return new ChatResponseDto
                {
                    Response      = "An internal error occurred while retrieving the query definition. Please try again.",
                    Data          = [],
                    SessionId     = sessionId,
                    Intent        = intent,
                    ResponseType  = "text_only",
                    ResponseLabel = "Internal error"
                };
            }

            var intentSql = definition.Sql;

            _logger.LogInformation(
                "Known Intent Route | Intent: {Intent} | Session: {Session}",
                intent, sessionId);

            await EmitStageAsync(
                stageKey: "tool_invocation",
                stageName: "Preparing tools",
                progressPercent: 60,
                status: "completed",
                estimatedMsRemaining: 1600,
                elapsedMs: totalStopwatch.ElapsedMilliseconds,
                technicalDetails: new Dictionary<string, string>
                {
                    ["tool"]   = "IntentRouterService",
                    ["status"] = "success"
                });

            await EmitStageAsync(
                stageKey: "query_building",
                stageName: "Preparing SQL query",
                progressPercent: 66,
                status: "completed",
                estimatedMsRemaining: 1400,
                elapsedMs: totalStopwatch.ElapsedMilliseconds,
                technicalDetails: new Dictionary<string, string>
                {
                    ["pipeline"]  = "predefined_sql",
                    ["intent"]    = intent,
                    ["sqlLength"] = intentSql.Length.ToString()
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
                    ["strategy"] = "intent_router",
                    ["intent"]   = intent
                });

            var intentParams = new
            {
                UserId       = userId,
                LcNumber     = entities.LcNumber,
                BankName     = entities.BankName,
                CustomerName = entities.CustomerName,
                Status       = entities.Status,
                MinAmount    = entities.MinAmount,
                MaxAmount    = entities.MaxAmount,
                DaysRange    = entities.DaysRange,
                StartDate    = entities.StartDate,
                EndDate      = entities.EndDate,
                Country      = entities.Country,
                CurrencyCode = entities.CurrencyCode,
                IsPendingStatus = entities.IsPendingStatus
            };

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
                        ["command"]    = "QueryAsync<dynamic>",
                        ["sqlPreview"] = intentSql.Length > 220
                            ? $"{intentSql[..220]}..."
                            : intentSql,
                        ["boundParameters"] = $"UserId={userId}"
                    });

                // Propagate cancellationToken to Dapper CommandDefinition so the SQL query
                // is cancelled cooperatively when the client disconnects.
                dbRows = (await _db.QueryAsync<dynamic>(intentSql, intentParams, cancellationToken)).ToList();
                _logger.LogInformation("Known Intent SQL returned {Count} rows", dbRows.Count);
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
                        ["executionMs"]  = sqlStopwatch.ElapsedMilliseconds.ToString()
                    });
            }
            catch (OperationCanceledException)
            {
                // Client disconnected – cancellation is expected, not a system error.
                _logger.LogInformation("SQL execution cancelled by client disconnect | Intent: {Intent}", intent);
                throw;
            }
            catch (Exception sqlEx)
            {
                _logger.LogError(sqlEx, "Known Intent SQL execution failed | Intent: {Intent}", intent);
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
                        ["sqlPreview"] = intentSql.Length > 220
                            ? $"{intentSql[..220]}..."
                            : intentSql
                    });

                return new ChatResponseDto
                {
                    Response      = "A database error occurred while retrieving your data. Please try again.",
                    Data          = [],
                    SessionId     = sessionId,
                    Intent        = intent,
                    ResponseType  = "text_only",
                    ResponseLabel = "Execution error",
                    IsTextToSql   = false,
                    ExecutedQuery = _showSqlQuery ? intentSql : null
                };
            }

            responseType  = ResponseTypeMap.TryGetValue(intent, out var rt) ? rt : "table";
            responseLabel = BuildResponseLabel(intent, dbRows.Count);
            executedSql   = _showSqlQuery ? BuildResolvedQuery(intentSql, entities, userId) : null;
            queryType     = "predefined";
            isTextToSql   = false;
        }
        else
        {
            // ── Dynamic AI SQL Route ──────────────────────────────────────────
            // Intent is not in IntentRouterService; delegate to SqlGenerationService.
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
                "Dynamic AI SQL Route | Session: {Session} | Message: {Message}",
                sessionId, request.Message);

            await EmitStageAsync(
                stageKey: "query_building",
                stageName: "Preparing SQL query",
                progressPercent: 56,
                estimatedMsRemaining: 1900,
                technicalDetails: new Dictionary<string, string>
                {
                    ["pipeline"]                     = "text_to_sql",
                    ["parameterBinding.userId"]      = userId.ToString(),
                    ["parameterBinding.lcNumber"]    = entities.LcNumber ?? "null",
                    ["parameterBinding.bankName"]    = entities.BankName ?? "null",
                    ["parameterBinding.customerName"]= entities.CustomerName ?? "null",
                    ["parameterBinding.status"]      = entities.Status ?? "null"
                });

            // Propagate cancellationToken so Azure OpenAI SQL generation stops if aborted.
            var genResult = await _sqlGeneration.GenerateSqlAsync(request.Message, userId, cancellationToken);

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
                    Response      = "I'm here to help with LC process questions — things like pending approvals, issued or expiring LCs, amendment history, invoice status, and trade finance data. Could you try rephrasing, or ask something about your letters of credit?",
                    Data          = [],
                    SessionId     = sessionId,
                    Intent        = intent,
                    ResponseType  = "text_only",
                    ResponseLabel = "Out of scope",
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
                    ["tool"]   = "SqlGenerationService",
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
                    ["queryType"]    = genResult.QueryType ?? "unknown",
                    ["responseType"] = genResult.ResponseType ?? "unknown",
                    ["sqlLength"]    = genResult.Sql.Length.ToString()
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
                    ["renderer"]  = genResult.ResponseType ?? "table",
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
                        ["command"]         = "QueryAsync<dynamic>",
                        ["sqlPreview"]      = genResult.Sql.Length > 220
                            ? $"{genResult.Sql[..220]}..."
                            : genResult.Sql,
                        ["boundParameters"] = $"UserId={userId}"
                    });

                // Propagate cancellationToken to Dapper CommandDefinition so the SQL query
                // is cancelled cooperatively when the client disconnects.
                dbRows = (await _db.QueryAsync<dynamic>(genResult.Sql, new { UserId = userId }, cancellationToken)).ToList();
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
                        ["executionMs"]  = sqlStopwatch.ElapsedMilliseconds.ToString()
                    });
            }
            catch (OperationCanceledException)
            {
                // Client disconnected – cancellation is expected, not a system error.
                _logger.LogInformation("Dynamic SQL execution cancelled by client disconnect");
                throw;
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
            executedSql   = _showSqlQuery ? genResult.Sql : null;
            queryType     = genResult.QueryType;
            isTextToSql   = true;
        }

        // ── Shared tail: result_processing → AI summary → persist → return ────
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
                ["queryType"]          = queryType ?? "unknown",
                ["rowCount"]           = dbRows.Count.ToString()
            });

        // ── AI natural language summary ────────────────────────────────────────
        ClarificationDto? clarification = null;
        string[] suggestedQuestions = [];
        string aiResponse;
        if (dbRows.Count == 0)
        {
            clarification = await _clarificationService.DetectAsync(
                request.Message, entities, userId, cancellationToken);

            if (clarification is not null)
            {
                aiResponse   = $"I couldn't find any data for \"{clarification.UnrecognisedValue}\". Did you mean one of these?";
                responseType = "clarification";
            }
            else
            {
                aiResponse   = BuildNoResultsMessage(entities);
                responseType = "text_only";
            }
        }
        else
        {
            // Run AI summary (with streaming) and suggested questions in parallel.
            var aiResponseTask  = _aiResponse.GenerateResponseAsync(request.Message, dbRows, null, onTokenReceived, cancellationToken);
            var suggestionsTask = _suggestedQuestionsService.GenerateAsync(request.Message, responseType, dbRows.Cast<object>(), cancellationToken);
            await Task.WhenAll(aiResponseTask, suggestionsTask);
            aiResponse         = aiResponseTask.Result;
            suggestedQuestions = suggestionsTask.Result;

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
                ["usedAiSummarizer"]    = (dbRows.Count > 0).ToString(),
                ["finalResponseLength"] = aiResponse.Length.ToString()
            });

        // ── Persist and return ─────────────────────────────────────────────────
        var serializedData = System.Text.Json.JsonSerializer.Serialize(dbRows);
        // Propagate cancellationToken to history save – if already cancelled the
        // INSERT is skipped cooperatively (DB connection is not abandoned).
        await _historyService.SaveMessageAsync(
            sessionId, "assistant", aiResponse,
            executedSql,
            intent, responseType, serializedData, queryType,
            cancellationToken);

        _logger.LogInformation(
            "Done | Session: {Session} | Rows: {Count} | RT: {RT}",
            sessionId, dbRows.Count, responseType);

        var finalResponse = new ChatResponseDto
        {
            Response           = aiResponse,
            Data               = dbRows,
            SessionId          = sessionId,
            ExecutedQuery      = executedSql,
            Intent             = intent,
            ResponseType       = responseType,
            ResponseLabel      = responseLabel,
            IsTextToSql        = isTextToSql,
            QueryType          = queryType,
            SuggestedQuestions = suggestedQuestions,
            Clarification      = clarification
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
                ["sessionId"]      = sessionId,
                ["totalElapsedMs"] = totalStopwatch.ElapsedMilliseconds.ToString(),
                ["responseType"]   = finalResponse.ResponseType ?? "unknown"
            });

        return finalResponse;
    }

    // ── DetermineRouting ──────────────────────────────────────────────────────
    /// <summary>
    /// Produces a deterministic <see cref="QueryRoutingDecision"/> based solely
    /// on whether IntentRouterService has a predefined SQL for the given intent.
    /// Known intents → IntentRouterService.
    /// Unknown intents → SqlGenerationService (AI fallback).
    /// </summary>
    private QueryRoutingDecision DetermineRouting(string intent)
    {
        // Always route through SqlGenerationService so every question goes through
        // the 4-phase AI pipeline, getting correct dynamic columns, responseType,
        // and chart selection instead of hardcoded IntentRouterService SQL.
        return new QueryRoutingDecision
        {
            UseIntentRouter = false,
            Intent          = intent,
            Reason          = "All questions routed to SqlGenerationService — intent router disabled."
        };
    }

    // ── BuildNoResultsMessage ─────────────────────────────────────────────────
    // Returns a clarification message when SQL returns 0 rows, naming the
    // specific filters that were applied so the user knows what to correct.
    private static string BuildNoResultsMessage(QueryEntitiesDto entities)
    {
        var filters = new List<string>();
        if (!string.IsNullOrWhiteSpace(entities.BankName))
            filters.Add($"bank \"{entities.BankName}\"");
        if (!string.IsNullOrWhiteSpace(entities.CustomerName))
            filters.Add($"customer \"{entities.CustomerName}\"");
        if (!string.IsNullOrWhiteSpace(entities.Status))
            filters.Add($"status \"{entities.Status}\"");
        if (!string.IsNullOrWhiteSpace(entities.LcNumber))
            filters.Add($"LC number \"{entities.LcNumber}\"");
        if (entities.StartDate.HasValue || entities.EndDate.HasValue)
        {
            var from = entities.StartDate.HasValue ? entities.StartDate.Value.ToString("dd MMM yyyy") : "…";
            var to   = entities.EndDate.HasValue   ? entities.EndDate.Value.ToString("dd MMM yyyy")   : "…";
            filters.Add($"date range {from} – {to}");
        }

        if (filters.Count == 0)
            return "No records found for your query. Try broadening the search criteria.";

        var joined = string.Join(", ", filters);
        return $"No records found for {joined}. " +
               "Please verify the details — the name or value might not exactly match what is in the system. " +
               "Try checking for alternate spellings, or ask with a broader filter.";
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

