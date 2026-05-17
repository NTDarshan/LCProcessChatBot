using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using System.Text.Json;

namespace backend.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  AiResponseService  –  converts raw database rows into a natural-language
//  response grounded strictly in the provided data (no hallucination).
//
//  FIXES IN THIS VERSION:
//  1.  Status code translation added to system prompt — AI now says
//      "Pending N+1 approval" not "Submitted_N+1".
//  2.  Stronger no-markdown enforcement — extra examples of what NOT to do.
//  3.  Aggregation-aware summary instructions added: when data is a count/chart
//      result (TopBanks, StatusBreakdown, CustomerLC), the AI should give a
//      concise insight paragraph about the distribution, not list every row.
//  4.  Empty result message made friendlier — says "No records found" not
//      "No data found for your query." (matches the new LcEmptyStateComponent).
//  5.  Amount formatting guidance: use "1.15M EUR" shorthand for large values.
//
//  CANCELLATION ENHANCEMENT:
//  CancellationToken is forwarded to CompleteChatAsync so that if the HTTP
//  request is aborted, the Azure OpenAI call is cancelled immediately —
//  preventing unnecessary token spend on discarded completions.
// ─────────────────────────────────────────────────────────────────────────────
public class AiResponseService
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<AiResponseService> _logger;

    public AiResponseService(IConfiguration config, ILogger<AiResponseService> logger)
    {
        _logger = logger;
        var endpoint   = config["OpenAI:Endpoint"]!;
        var key        = config["OpenAI:Key"]!;
        var deployment = config["OpenAI:Deployment"]!;
        var client     = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        _chatClient    = client.GetChatClient(deployment);
    }

    // Generates a concise professional summary grounded strictly in the DB data.
    // conversationHistory: optional last few turns for follow-up context.
    // cancellationToken: forwarded to Azure OpenAI so the completion call is
    // cancelled immediately when the client disconnects, preventing wasted tokens.
    public async Task<string> GenerateResponseAsync(
        string userQuestion,
        object dbResult,
        IEnumerable<(string Role, string Content)>? conversationHistory = null,
        CancellationToken cancellationToken = default)
    {
        return await GenerateResponseAsync(userQuestion, dbResult, conversationHistory, null, cancellationToken);
    }

    public async Task<string> GenerateResponseAsync(
        string userQuestion,
        object dbResult,
        IEnumerable<(string Role, string Content)>? conversationHistory,
        Func<string, Task>? onTokenReceived,
        CancellationToken cancellationToken = default)
    {
        var jsonData = JsonSerializer.Serialize(dbResult, new JsonSerializerOptions
        {
            WriteIndented    = false,
            NumberHandling   = System.Text.Json.Serialization.JsonNumberHandling.Strict
        });

        var systemPrompt = """
            You are an enterprise assistant for an LC (Letter of Credit) trade-finance
            management system used by ArcelorMittal International Luxembourg.

            ═══════════════════════════════════════════════════════
            DOMAIN KNOWLEDGE
            ═══════════════════════════════════════════════════════
            LC TYPES: UPAS (Usance Payable At Sight), At Sight, USANCE.

            KEY DATES:
            - LcIssueDate / lc_issue_date   = date the bank issued the LC
            - LcExpiryDate / lc_expire_date = date the LC expires
            - GracePeriod / grace_period    = last date bank will accept documents
            - AmiPaymentDate                = ArcelorMittal expected payment date
            - ActualShipmentDate            = date goods were shipped

            STATUS CODES — always translate to human labels in your response:
            - Draft                    → "Draft"
            - Submitted_For_Validation → "submitted for validation"
            - Submitted_N+1            → "pending N+1 approval"
            - Submitted_N+2            → "pending N+2 approval"
            - LCIssued                 → "issued"
            - PaymentDone              → "paid"
            - PaymentNotDone           → "unpaid / outstanding"
            - Rejected                 → "rejected"
            - Cancelled                → "cancelled"
            NOTE: The data may also contain a "StatusLabel" or "HumanLabel" field
            that already has the translated value — use that if present.

            BANKS: BNP = BNP Paribas Fortis, KBC = KBC Bank, CACIB = Credit Agricole CIB,
                   COMMERZBANK = Commerzbank.

            CURRENCIES: always include the code — EUR, USD, AED, INR, AZN, CAD, DKK.

            ═══════════════════════════════════════════════════════
            STRICT RESPONSE RULES
            ═══════════════════════════════════════════════════════
            1. Use ONLY the data in "Database result" below. Never invent figures.
            2. If the database result is empty or null, respond with EXACTLY:
               "No records found"
               Nothing else.
            3. Write 2–5 plain prose sentences. No more.
            4. Always translate status codes using the mapping above.
            5. Format large amounts as "EUR 1.15M" or "USD 6.9M". For smaller
               amounts use "EUR 250,000". Always include the currency code.
            6. For aggregation results (counts by bank, status, customer):
               - Lead with the total count across all rows.
               - Name the top 1–2 entries by value/count with their figures.
               - Mention any notable outliers or patterns if supported by data.
            7. Mention amendment count if AmendmentCount > 0.
            8. Highlight expiry dates and grace periods when present.
            9. Do NOT mention SQL, database, tables, columns, or technical details.
            10. Do NOT add suggestions, warnings, or disclaimers beyond the data.

            ═══════════════════════════════════════════════════════
            FORMATTING RULES — CRITICAL
            ═══════════════════════════════════════════════════════
            - NEVER use markdown tables. Not a single pipe | character.
            - NEVER use markdown headers: no ##, no #, no ###.
            - NEVER use **bold** or *italic* markdown syntax.
            - NEVER use bullet lists (- item) or numbered lists (1. item).
            - NEVER use code blocks or backticks.
            - Write ONLY plain flowing prose sentences.
            - The frontend renders the actual data rows in visual cards/charts/tables.
              Your job is ONLY the 2–5 sentence insight paragraph above those visuals.

            GOOD response example (for pending approvals):
            "There are 3 LC requests currently pending approval. Two are with BNP bank
            (Billets for Aceria del Ecuador, EUR 2M; and Aluminium Sheets for
            Customer-test1, EUR 2.5M) and one with KBC. All are at N+1 or N+2 stage
            and were submitted within the last 7 days."

            BAD response examples (never do these):
            "| LC Number | Bank | Amount |" ← pipe table — FORBIDDEN
            "## Pending Approvals" ← header — FORBIDDEN
            "- BNP: EUR 2M" ← bullet list — FORBIDDEN
            "**BNP** has the highest value" ← bold — FORBIDDEN
            """;

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt)
        };

        // Inject recent conversation turns so model can answer follow-ups
        if (conversationHistory is not null)
        {
            foreach (var (role, content) in conversationHistory)
            {
                if (role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    messages.Add(new UserChatMessage(content));
                else
                    messages.Add(new AssistantChatMessage(content));
            }
        }

        var userPrompt = $"""
            User question:
            {userQuestion}

            Database result (JSON):
            {jsonData}

            Write a 2–5 sentence plain prose insight summary. Translate all status codes.
            No markdown, no lists, no tables.
            """;

        messages.Add(new UserChatMessage(userPrompt));

        try
        {
            var fullResponse = new System.Text.StringBuilder();

            if (onTokenReceived is not null)
            {
                await foreach (var update in _chatClient.CompleteChatStreamingAsync(messages, cancellationToken: cancellationToken))
                {
                    if (update.ContentUpdate.Count > 0)
                    {
                        var token = update.ContentUpdate[0].Text;
                        if (!string.IsNullOrEmpty(token))
                        {
                            fullResponse.Append(token);
                            await onTokenReceived(token);
                        }
                    }
                }
            }
            else
            {
                var response = await _chatClient.CompleteChatAsync(messages, cancellationToken: cancellationToken);
                fullResponse.Append(response.Value.Content[0].Text);
            }

            return fullResponse.ToString().Trim();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("AI response generation cancelled by client disconnect");
            throw;
        }
    }
}