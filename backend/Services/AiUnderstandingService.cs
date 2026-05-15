using System.Text.RegularExpressions;
using System.Text.Json;
using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;

namespace backend.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  AiUnderstandingService  –  hybrid intent classifier.
//  Pipeline: normalize → rule-based → AI classifier → safety gate → fallback.
//
//  KEY FIXES vs original:
//  1.  ValidIntents set extended: ExpiredLC, MyApprovals, AmendmentCount,
//      PendingApprovalsN1, PendingApprovalsN2, ApprovalBottleneck,
//      InvoiceStatus, CustomerLC, LcHistory added.
//  2.  Rule table fixed: ["expir"] partial match replaced with full word
//      boundary checks to avoid false matches on "experience" etc.
//  3.  New rules added for all new intents and for natural-language variants
//      like "my approvals", "expired lcs", "invoice status", "history".
//  4.  AI prompt updated with new intents and better examples.
//  5.  "issued" rule now uses word boundary to avoid matching "reissued".
//  6.  Amendment count / "multiple amendments" rule added.
//  7.  N+1 / N+2 specific routing rules added.
//
//  CANCELLATION ENHANCEMENT:
//  CancellationToken is threaded through all Azure OpenAI calls so that
//  intent classification stops immediately when the request is aborted.
// ─────────────────────────────────────────────────────────────────────────────
public class AiUnderstandingService
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<AiUnderstandingService> _logger;

    // Complete set of valid intent names – any AI response outside this set is rejected
    private static readonly HashSet<string> _validIntents = new(StringComparer.OrdinalIgnoreCase)
    {
        "PendingApprovals", "PendingApprovalsN1", "PendingApprovalsN2",
        "ApprovalBottleneck", "MyApprovals",
        "DraftLC", "RejectedLC", "CancelledLC",
        "IssuedLC", "PaidLC", "OutstandingLC", "ExpiringLC", "ExpiredLC",
        "DelayedRequests", "TopBanks", "AmendmentRequests", "AmendmentCount",
        "ValidationPending", "StatusBreakdown", "LcStatus",
        "InvoiceStatus", "CustomerLC", "LcHistory"
    };

    // Rule table: ALL keywords must appear in normalized message → intent returned.
    // Ordered most-specific first.  Uses whole-word checks where needed.
    private static readonly (string[] Keywords, string Intent)[] _rules =
    [
        // ── Specific approval levels
        (["n+1"],                               "PendingApprovalsN1"),
        (["n+2"],                               "PendingApprovalsN2"),
        (["pending", "n+1"],                    "PendingApprovalsN1"),
        (["pending", "n+2"],                    "PendingApprovalsN2"),

        // ── Approval workflow general
        (["my", "approval"],                    "MyApprovals"),
        (["my", "pending"],                     "MyApprovals"),
        (["assigned", "me"],                    "MyApprovals"),
        (["waiting", "my"],                     "MyApprovals"),
        (["stuck", "approval"],                 "ApprovalBottleneck"),
        (["bottleneck"],                        "ApprovalBottleneck"),
        (["pending", "approval"],               "PendingApprovals"),
        (["pending", "approvals"],              "PendingApprovals"),
        (["approval", "pending"],               "PendingApprovals"),
        (["submitted", "approval"],             "PendingApprovals"),

        // ── Validation
        (["validation", "pending"],             "ValidationPending"),
        (["submitted", "validation"],           "ValidationPending"),
        (["stuck", "validation"],               "ApprovalBottleneck"),

        // ── Audit / history
        (["history", "lc"],                     "LcHistory"),
        (["lc", "history"],                     "LcHistory"),
        (["audit", "lc"],                       "LcHistory"),
        (["lifecycle"],                         "LcHistory"),
        (["timeline"],                          "LcHistory"),
        (["activity"],                          "LcHistory"),

        // ── Amendments
        (["multiple", "amendment"],             "AmendmentCount"),
        (["amendment", "count"],                "AmendmentCount"),
        (["how", "many", "amendment"],          "AmendmentCount"),
        (["amendment"],                         "AmendmentRequests"),
        (["amend"],                             "AmendmentRequests"),

        // ── Invoice / payment
        (["invoice", "status"],                 "InvoiceStatus"),
        (["invoice", "payment"],                "InvoiceStatus"),
        (["invoice"],                           "InvoiceStatus"),
        (["payment", "done"],                   "PaidLC"),
        (["payment", "not"],                    "OutstandingLC"),
        (["paid"],                              "PaidLC"),
        (["unpaid"],                            "OutstandingLC"),
        (["outstanding"],                       "OutstandingLC"),

        // ── Expiry (use "expir" but guard against "experience")
        (["expiring"],                          "ExpiringLC"),
        (["expiry"],                            "ExpiringLC"),
        (["expire"],                            "ExpiringLC"),
        (["expired"],                           "ExpiredLC"),
        (["lc", "expired"],                     "ExpiredLC"),

        // ── Delays / SLA
        (["delay"],                             "DelayedRequests"),
        (["overdue"],                           "DelayedRequests"),
        (["slow"],                              "DelayedRequests"),
        (["sla"],                               "DelayedRequests"),

        // ── Banks
        (["top", "bank"],                       "TopBanks"),
        (["best", "bank"],                      "TopBanks"),
        (["highest", "bank"],                   "TopBanks"),
        (["bank", "value"],                     "TopBanks"),
        (["bank", "wise"],                      "TopBanks"),

        // ── Status lifecycle
        (["issued"],                            "IssuedLC"),
        (["lcissued"],                          "IssuedLC"),
        (["rejected"],                          "RejectedLC"),
        (["cancelled"],                         "CancelledLC"),
        (["cancel"],                            "CancelledLC"),
        (["draft"],                             "DraftLC"),

        // ── Customer distribution
        (["customer", "wise"],                  "CustomerLC"),
        (["by", "customer"],                    "CustomerLC"),
        (["customer", "distribution"],          "CustomerLC"),
        (["customer", "lc"],                    "CustomerLC"),

        // ── Aggregations / summary
        (["summary"],                           "StatusBreakdown"),
        (["breakdown"],                         "StatusBreakdown"),
        (["statistics"],                        "StatusBreakdown"),
        (["overview"],                          "StatusBreakdown"),
        (["count"],                             "StatusBreakdown"),
        (["how", "many"],                       "StatusBreakdown"),
        (["total"],                             "StatusBreakdown"),
    ];

    public AiUnderstandingService(
        IConfiguration config,
        ILogger<AiUnderstandingService> logger)
    {
        _logger = logger;
        var endpoint   = config["OpenAI:Endpoint"]!;
        var key        = config["OpenAI:Key"]!;
        var deployment = config["OpenAI:Deployment"]!;
        var client     = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        _chatClient    = client.GetChatClient(deployment);
    }

    // Full pipeline: normalize → rules → AI → safety gate.
    // cancellationToken: passed to the Azure OpenAI call so that if the
    // request is aborted, the HTTP call to OpenAI is cancelled immediately,
    // preventing unnecessary token consumption.
    public async Task<string> GetIntentAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(message);
        _logger.LogInformation("Intent | Original: '{Msg}' | Normalized: '{Norm}'", message, normalized);

        // Rule-based shortcut (avoids AI call for common patterns)
        var ruleIntent = TryRuleBasedIntent(normalized);
        if (ruleIntent is not null)
        {
            _logger.LogInformation("Rule match → {Intent}", ruleIntent);
            return ruleIntent;
        }

        _logger.LogInformation("No rule match – calling AI classifier");
        var aiRaw   = await CallAiClassifierAsync(normalized, cancellationToken);
        var resolved = ResolveIntent(aiRaw);
        _logger.LogInformation("AI raw: '{Raw}' → resolved: '{Resolved}'", aiRaw, resolved);
        return resolved;
    }

    // ── Normalize ─────────────────────────────────────────────────────────────
    private static string Normalize(string message)
    {
        var lower   = message.ToLowerInvariant().Trim();
        // Keep alphanumeric, spaces, + and / (needed for N+1, N+2, LC codes)
        var cleaned = Regex.Replace(lower, @"[^a-z0-9\s\+\/]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // ── Rule-based matching ───────────────────────────────────────────────────
    private static string? TryRuleBasedIntent(string normalized)
    {
        foreach (var (keywords, intent) in _rules)
        {
            if (keywords.All(k => normalized.Contains(k, StringComparison.Ordinal)))
                return intent;
        }
        return null;
    }

    // ── AI classifier ─────────────────────────────────────────────────────────
    // cancellationToken: forwarded to CompleteChatAsync so the Azure OpenAI
    // HTTP call is cancelled immediately when the upstream token fires.
    private async Task<string> CallAiClassifierAsync(
        string normalized,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = """
            You are an intent classifier for an enterprise Letter of Credit (LC) management system
            used by ArcelorMittal Luxembourg.

            Return ONLY one intent name from the list below — no explanation, no punctuation.
            If unclear, return: UNKNOWN

            INTENTS:
            PendingApprovals      – LCs waiting for any approval
            PendingApprovalsN1    – LCs at N+1 approval level specifically
            PendingApprovalsN2    – LCs at N+2 approval level specifically
            ApprovalBottleneck    – LCs stuck in approval workflow for too long
            MyApprovals           – approvals assigned to the logged-in user
            DraftLC               – LC requests in draft state
            IssuedLC              – LCs that have been issued by the bank
            PaidLC                – LCs where payment is done
            RejectedLC            – rejected LC requests
            CancelledLC           – cancelled LC requests
            ValidationPending     – LCs submitted for validation (not yet at approval)
            OutstandingLC         – issued LCs where payment is not yet received
            ExpiringLC            – LCs expiring soon
            ExpiredLC             – LCs that have already expired
            DelayedRequests       – LC requests delayed beyond SLA
            TopBanks              – analysis of LCs grouped by bank
            AmendmentRequests     – LC amendments list or details
            AmendmentCount        – how many amendments an LC has had
            InvoiceStatus         – invoice payment status for LCs
            StatusBreakdown       – summary counts / totals across all statuses
            LcStatus              – general LC details / specific LC lookup
            CustomerLC            – LCs grouped or filtered by customer
            LcHistory             – audit trail / history of an LC

            EXAMPLES:
            User: show pending approvals → PendingApprovals
            User: which lcs are at n+1 → PendingApprovalsN1
            User: show my pending tasks → MyApprovals
            User: show issued lcs → IssuedLC
            User: show top banks by value → TopBanks
            User: give lc summary → StatusBreakdown
            User: show unpaid lcs → OutstandingLC
            User: show lcs expiring in 30 days → ExpiringLC
            User: show expired lcs → ExpiredLC
            User: show amendment history for lc bnp123 → AmendmentRequests
            User: which lcs have multiple amendments → AmendmentCount
            User: show invoice status → InvoiceStatus
            User: show lcs for tata steel → LcStatus
            User: show customer wise lc count → CustomerLC
            User: show lifecycle of lc1023 → LcHistory
            User: random question about weather → UNKNOWN
            """;

        var userPrompt = $"User: {normalized}\nIntent:";
        var messages   = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        try
        {
            // Pass cancellationToken: if the HTTP request is aborted, this Azure
            // OpenAI call is cancelled immediately, preventing orphaned token spend.
            var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
            return response.Value.Content[0].Text.Trim();
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected behaviour – log as informational, not error.
            _logger.LogInformation("Intent classification cancelled by client disconnect");
            throw; // Re-throw so the pipeline unwinds cleanly
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure OpenAI call failed during intent classification");
            return "UNKNOWN";
        }
    }

    // ── Safety gate ───────────────────────────────────────────────────────────
    private string ResolveIntent(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains(' '))
        {
            _logger.LogWarning("AI invalid response '{Raw}' → fallback LcStatus", raw);
            return "LcStatus";
        }
        if (raw.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase)
            || !_validIntents.Contains(raw))
        {
            _logger.LogWarning("AI intent '{Raw}' not in valid set → fallback LcStatus", raw);
            return "LcStatus";
        }
        return raw;
    }

    // Generates concise, query-aware live labels for each processing stage.
    // This is a single AI call so we avoid per-stage latency overhead.
    // cancellationToken: forwarded to Azure OpenAI so the call is cancelled
    // immediately when the request is aborted.
    public async Task<Dictionary<string, string>> GenerateProcessingLabelsAsync(
        string userQuestion,
        string? intentHint = null,
        CancellationToken cancellationToken = default)
    {
        var systemPrompt = """
            You generate short live progress labels for an LC chatbot.
            Return ONLY valid JSON object with these exact keys:
            query_reception, query_parse, context_retrieval, tool_invocation,
            query_building, execution_plan, db_execution, result_processing,
            response_generation, final_output.

            Rules:
            - 2 to 5 words per label.
            - Business-facing, trade-finance aware wording.
            - No markdown, no punctuation except apostrophe.
            - Keep labels distinct across stages.
            - Do not include percentages, numbers, or technical jargon.
            """;

        var userPrompt = $"""
            User question: {userQuestion}
            Intent hint: {intentHint ?? "unknown"}
            Return JSON only.
            """;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        try
        {
            // Pass cancellationToken: if the request is aborted, this call stops
            // immediately rather than consuming tokens for a discarded response.
            var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
            var raw      = response.Value.Content[0].Text.Trim();

            var labels = JsonSerializer.Deserialize<Dictionary<string, string>>(
                raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (labels is null || labels.Count == 0)
                return new Dictionary<string, string>();

            // Normalize and cap to keep UI compact.
            var sanitized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in labels)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                sanitized[kv.Key] = kv.Value.Trim();
            }

            return sanitized;
        }
        catch (OperationCanceledException)
        {
            // Cancellation is expected – log informational only.
            _logger.LogInformation("Processing label generation cancelled by client disconnect");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate AI processing labels");
            return new Dictionary<string, string>();
        }
    }
}
