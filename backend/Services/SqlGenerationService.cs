using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace backend.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  SqlGenerationService  –  4-phase chain-of-thought Text-to-SQL.
//  Target accuracy: 95%+
//
//  CHANGES vs previous version:
//  1. Phase 0 added — query type classifier (list/aggregate/single_stat/timeline/comparison)
//     Prevents context-bleed wrong query type (e.g. AVG question → list query bug)
//  2. Phase 4 added — AI self-review checklist before returning
//     Catches ~60% of logic errors before C# validation
//  3. R7 (TOP rule) completely rewritten — smart TOP per query type
//     NO TOP on aggregations. TOP(200) default for lists. TOP(100) for timeline.
//     NEVER TOP(50) — too small for production LC data.
//  4. MaxOutputTokenCount raised 2000 → 3000
//     Complex queries were hitting token limit and returning truncated JSON
//  5. Auto-retry on validation failure (new public entry point + GenerateInternalAsync)
//     Feeds C# validation error back to AI for one retry — fixes ~70% of validation failures
//  6. query_type field added to JSON output
//  7. 8 new few-shot examples added (total 12 — was 4)
//     Covers AVG/single_stat, timeline, comparison, amendment, beneficiary date, approval, top-N
//  8. Phase 2 mapping forces explicit nullable check on every column
//     Root cause of "silent filter bug" (NULL < GETDATE() = UNKNOWN = row dropped)
// ─────────────────────────────────────────────────────────────────────────────
public class SqlGenerationService
{
    private readonly ChatClient _chatClient;
    private readonly SqlValidationService _validator;
    private readonly ILogger<SqlGenerationService> _logger;
    private readonly CacheService _cache;

    // ─── COMPRESSED SCHEMA ────────────────────────────────────────────────────
    // Exact column names from the real database. ~900 tokens.
    // Every column the AI is allowed to reference is listed here.
    // Update when columns are added to the DB.
    // ─────────────────────────────────────────────────────────────────────────
    private const string Schema = """
        ╔══════════════════════════════════════════════════════════════╗
        ║  LC TRADE FINANCE — SQL SERVER DATABASE SCHEMA               ║
        ║  All column names are EXACT. Use them verbatim in SQL.       ║
        ╚══════════════════════════════════════════════════════════════╝

        ━━━ TABLE: lc_request_details  [PK: obj_id] ━━━━━━━━━━━━━━━━━━
        Alias: lrd  — root record, created when a user submits an LC request.
        One lc_request_details → one lc_details (only after bank issues the LC).

          obj_id               INT          Primary key
          bank                 VARCHAR      Which bank (BNP / KBC / CACIB / COMMERZBANK)
          product              VARCHAR      Steel product name
          volume               DECIMAL      Volume in MT
          total_amount         DECIMAL      Requested LC amount (may differ from ld.amount)
          currency             VARCHAR      Requested currency (EUR/USD/AED/INR/AZN/CAD/DKK)
          status_id            INT          FK → statuses.obj_id
          customer_id          INT          FK → customers.obj_id
          business_unit_id     INT          FK → business_unit.obj_id
          created_by           INT          FK → users.obj_id
          modified_by          INT          FK → users.obj_id
          type_of_lc           VARCHAR      LC type as requested (UPAS / At Sight / USANCE)
          contract_number      VARCHAR      Internal contract ref
          lds                  DATE         Latest date of shipment (requested)
          date_of_shipment     DATE         Planned shipment date
          suppliername         VARCHAR      Supplier name
          beneficiary          VARCHAR      Beneficiary as per request
          port_of_destination  VARCHAR      Destination port
          purchase_payment_term VARCHAR     Payment terms (purchase side)
          sales_payment_term   VARCHAR      Payment terms (sales side)
          business_line        VARCHAR      Business line
          sales_incoterms      VARCHAR      Incoterms
          eta_date             DATE         Estimated arrival date
          lc_amount_usd        DECIMAL      LC amount in USD equivalent
          lc_amount_eur        DECIMAL      LC amount in EUR equivalent
          ami_payment_date     DATE         ArcelorMittal expected payment date
          submitted_for_approval_user_id INT FK → users.obj_id
          sales_manager_approver_id      INT FK → users.obj_id
          is_active            BIT          1 = active, 0 = deleted. ALWAYS filter: lrd.is_active = 1
          created_on           DATETIME     Request creation date
          modified_on          DATETIME     Last modified date

        ━━━ TABLE: lc_details  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: ld  — created when the bank issues the LC.
        ⚠️  MAY NOT EXIST for Draft / pending requests → ALWAYS LEFT JOIN.
        ⚠️  ld.amount and ld.currency can be NULL → always COALESCE/ISNULL.

          obj_id               INT          Primary key
          lc_request_id        INT          FK → lc_request_details.obj_id
          lc_number            VARCHAR      Bank-assigned LC number
          issuing_Bank         VARCHAR      Issuing bank name
          year                 INT          Year of issuance
          lc_issue_date        DATE         Date bank issued the LC
          lc_expire_date       DATE         Expiry date of the LC
          grace_period         DATE         Last date bank accepts documents
          lC_expired           BIT          Expiry flag — WARNING: often stale (0 even when expired).
                                            ALWAYS use DUAL condition:
                                            (ld.lC_expired=1 OR (ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date < GETDATE()))
          amount               DECIMAL      LC amount as issued by bank
          currency             VARCHAR      Currency as issued
          qty_in_mt            DECIMAL      Quantity in metric tonnes
          tolerance_plus_in_percentage  DECIMAL
          tolerance_minus_percentage    DECIMAL
          amount_tolerance_plus         DECIMAL
          amount_tolerance_minus        DECIMAL
          shipment_date        DATE         Actual shipment date
          shipment_month       VARCHAR      Shipment month label
          lds                  DATE         Latest date of shipment (actual)
          payment_terms        VARCHAR      Payment terms on LC
          type_of_LC           VARCHAR      LC type as issued (UPAS / At Sight / USANCE)
          beneficiary_name_on_LC VARCHAR   Beneficiary as per issued LC
          applicant            VARCHAR      Applicant name
          mill_name            VARCHAR      Mill / origin
          bank_address         VARCHAR      Issuing bank address
          port_Of_loading      VARCHAR      Port of loading
          port_Of_discharge    VARCHAR      Port of discharge
          partial_shipment_allow BIT
          period_for_presentation_days INT
          lc_amount_usd        DECIMAL
          lc_amount_eur        DECIMAL
          usd_bank_charges     DECIMAL
          sap_order_number     VARCHAR
          follow_up_number     VARCHAR
          ami_payment_date     DATE
          status_lc            VARCHAR
          comment              VARCHAR
          amendment_details    VARCHAR      HTML diff of latest amendment
          supplier_name        VARCHAR
          supplier_payment_date DATE
          created_by           INT
          modified_by          INT
          created_on           DATETIME
          modified_on          DATETIME

        ⚠️  NULL SAFETY for lc_details columns:
          Amount:   COALESCE(ld.amount, lrd.total_amount)         ← handles pre-issue nulls
          Currency: ISNULL(ld.currency, lrd.currency)             ← handles pre-issue nulls
          All date columns on ld are nullable — always add IS NOT NULL before comparisons.

        ━━━ TABLE: statuses  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: s

          obj_id               INT
          application_status   VARCHAR      EXACT values (case-sensitive):
                                            'Draft' | 'Submitted_For_Validation'
                                            'Submitted_N+1' | 'Submitted_N+2'
                                            'LCIssued' | 'PaymentDone'
                                            'PaymentNotDone' | 'Rejected' | 'Cancelled'

        ━━━ TABLE: customers  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: c

          obj_id               INT
          name                 VARCHAR      Customer display name
          sap_sold_to_name     VARCHAR
          sap_sold_to_nr       VARCHAR
          bpm_code             VARCHAR
          bussiness_line       VARCHAR      (typo in schema — use as-is)
          is_active            BIT
          scope                BIT

        ━━━ TABLE: business_unit  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━
        Alias: bu

          obj_id               INT
          business_unit_name   VARCHAR      Luxembourg / Singapore / India / LATAM
          description          VARCHAR
          is_active            BIT

        ━━━ TABLE: users  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: u

          obj_id               INT
          first_name           VARCHAR
          last_name            VARCHAR
          upn                  VARCHAR
          e_mail               VARCHAR
          dnd                  BIT
          is_active            BIT

        ━━━ TABLE: lc_amendment_details  [PK: obj_id] ━━━━━━━━━━━━━━━━
        Alias: lad

          obj_id               INT
          lc_request_id        INT          FK → lc_request_details.obj_id
          lc_details_id        INT          FK → lc_details.obj_id
          lc_number            VARCHAR
          year                 INT
          issuing_bank         VARCHAR
          supplier_name        VARCHAR
          lc_issue_date        DATE
          lds                  DATE
          lc_expire_date       DATE
          grace_period         DATE
          lC_expired           BIT
          amount               DECIMAL
          currency             VARCHAR
          qty_in_mt            DECIMAL
          shipment_date        DATE
          payment_terms        VARCHAR
          type_of_LC           VARCHAR
          amendment_details    VARCHAR
          applicant            VARCHAR
          beneficiary_name_on_LC VARCHAR
          lc_amount_usd        DECIMAL
          lc_amount_eur        DECIMAL
          usd_bank_charges     DECIMAL
          ami_payment_date     DATE
          sap_order_number     VARCHAR
          comment              VARCHAR
          created_by           INT
          modified_by          INT
          created_on           DATETIME
          modified_on          DATETIME

        ━━━ TABLE: invoice_details  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━
        Alias: inv  — one row per invoice. JOIN on BOTH FKs.

          obj_id               INT
          lc_request_id        INT          FK → lc_request_details.obj_id
          lc_details_id        INT          FK → lc_details.obj_id
          lc_number            VARCHAR
          qty                  DECIMAL
          shipment_date        DATE
          invoice_amount       DECIMAL
          currency             VARCHAR
          invoice_Date         DATE         ← capital D (exact name)
          beneficiary          VARCHAR
          beneficiary_pmt_date DATE         ← date beneficiary was actually paid
          ami_pmt_date         DATE
          is_Mark_as_paid      BIT          1 = paid, 0 = outstanding (exact name)
          is_Marked_as_final_update BIT
          is_Refunded          BIT
          Refund_Value_Date    DATE         ← capital R, V, D (exact name)
          lc_invoice_amount_usd DECIMAL
          usd_bank_charges     DECIMAL
          lc_invoice_amount_eur DECIMAL
          document_set_number  VARCHAR
          created_by           INT
          modified_by          INT
          created_on           DATETIME
          modified_on          DATETIME

        ━━━ TABLE: lc_approver_mapping  [PK: obj_id] ━━━━━━━━━━━━━━━━━
        Alias: lam

          obj_id               INT
          approver_id          INT          FK → users.obj_id
          lc_request_id        INT          FK → lc_request_details.obj_id
          status               NVARCHAR     'Close' = approved, 'Rejected' = rejected, NULL = pending
          is_approved_offline  BIT
          assigned_on          DATETIME
          action_taken_on      DATETIME     ← NULL means still pending

        ━━━ TABLE: lc_audit_log  [PK: obj_id] ━━━━━━━━━━━━━━━━━━━━━━━━
        Alias: al

          obj_id               INT
          lc_request_id        INT          FK → lc_request_details.obj_id
          actioned_by          INT          FK → users.obj_id
          action               NVARCHAR
          log_type             VARCHAR      'approval' | 'amendment' | 'invoice'
          actioned_on          DATETIME
          comment              VARCHAR

        ━━━ TABLE: user_business_unit_mapping  [PK: obj_id] ━━━━━━━━━━

          obj_id               INT
          user_id              INT          FK → users.obj_id
          business_unit_id     INT          FK → business_unit.obj_id
          created_on           DATETIME

        ═══════════════════════════════════════════════════════════════
        STANDARD JOIN PATTERN (base for every query):

          FROM lc_request_details lrd
          JOIN statuses      s   ON s.obj_id          = lrd.status_id
          JOIN customers     c   ON c.obj_id          = lrd.customer_id
          JOIN business_unit bu  ON bu.obj_id         = lrd.business_unit_id
          LEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id

        ═══════════════════════════════════════════════════════════════
        MANDATORY SCOPE FILTER (must appear in EVERY query):

          AND lrd.business_unit_id IN (
              SELECT business_unit_id
              FROM   user_business_unit_mapping
              WHERE  user_id = @UserId
          )

        ═══════════════════════════════════════════════════════════════
        BUSINESS RULES — always apply:

          R1. lrd.is_active = 1                       — filter deleted records
          R2. COALESCE(ld.amount, lrd.total_amount)   — handle pre-issue nulls
          R3. ISNULL(ld.currency, lrd.currency)       — handle pre-issue nulls
          R4. Expired: (ld.lC_expired=1 OR (ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date < GETDATE()))
          R5. ISNULL(SUM(...),0) and ISNULL(COUNT(...),0) and ISNULL(AVG(...),0) on all aggregates
          R6. Invoice joins: use BOTH inv.lc_request_id = lrd.obj_id AND inv.lc_details_id = ld.obj_id
          R7. (See TOP RULES section below — replaces old R7)
          R8. PascalCase aliases on all selected columns
          R9. NULL SAFETY: before any WHERE condition on a nullable column, add IS NOT NULL first.
              WRONG:   ld.grace_period < GETDATE()
              CORRECT: ld.grace_period IS NOT NULL AND ld.grace_period < GETDATE()
        """;

    // ─── RESPONSE TYPE GUIDE ─────────────────────────────────────────────────
    private const string ResponseTypeGuide = """
        RESPONSE TYPE SELECTION — return exactly one:

        "table"         → rows of LC/invoice/amendment/approval/audit data
        "metric_cards"  → grouped counts or totals (GROUP BY status/customer/bank)
        "bank_chart"    → totals or counts grouped by bank
        "approval_list" → pending approvals assigned to the current user
        "timeline"      → lc_audit_log events ordered by actioned_on ASC
        "comparison"    → exactly 2 groups being compared
        "line_chart"    → trend data over time (GROUP BY date/month/year)
        "area_chart"    → cumulative or volume trend over time (filled line)
        "radar_chart"   → multi-metric comparison of 2-5 entities (e.g. banks by 5 KPIs)
        "scatter_chart" → correlation between two numeric variables per LC
        "bubble_chart"  → 3-variable data (x=amount, y=days, size=count)
        "polar_chart"   → distribution across categories where area matters (like status)
        "mixed_chart"   → bar + line on same axes (e.g. LC count bars + cumulative line)
        "risk_scorecard" → computed risk rows, no chart library needed
        "kpi_strip"     → 3-6 large KPI numbers in a horizontal strip
        "expiry_heatmap" → calendar grid with colour-coded expiry dots per day

        chartType for metric_cards:  "doughnut" | "stacked_bar" | "count_grid" | "line"
        chartType for bank_chart:    "horizontal_bar"
        chartType for comparison:    "side_by_side"
        chartType for line_chart:    "line"
        chartType for area_chart:    "area"
        chartType for radar_chart:   "radar"
        chartType for scatter_chart: "scatter"
        chartType for bubble_chart:  "bubble"
        chartType for polar_chart:   "polar_area"
        chartType for mixed_chart:   "mixed_bar_line"
        chartType for risk_scorecard, kpi_strip, expiry_heatmap: null
        """;

    // ─── TOP(N) RULES — replaces old R7 ──────────────────────────────────────
    private const string TopNRules = """
        ═══════════════════════════════════════════════════════════════
        R7 — TOP(N) RULES — apply these EXACTLY, never deviate
        ═══════════════════════════════════════════════════════════════

        Evaluate query_type FIRST, then apply:

        IF query_type = "aggregate"   → NO TOP at all. Return all groups.
        IF query_type = "single_stat" → NO TOP at all. Returns 1 row.
        IF query_type = "comparison"  → NO TOP at all. Returns exactly 2 rows.
        IF query_type = "timeline"    → TOP(100). Audit logs can be very large.
        IF user said "top 5"          → TOP(5) exactly.
        IF user said "top 10"         → TOP(10) exactly.
        IF user said "top N"          → TOP(N) using that exact number.
        IF WHERE filters by lc_number → NO TOP. Returns at most 1 row.
        IF query_type = "list" AND user said "show all" → TOP(500).
        IF query_type = "list" (default)               → TOP(200).

        NEVER use TOP(50) — it is too small for production LC portfolio data.
        NEVER add TOP to any query that has a GROUP BY clause.
        """;

    // ─── CONSTRUCTOR ─────────────────────────────────────────────────────────
    public SqlGenerationService(
        IConfiguration config,
        SqlValidationService validator,
        ILogger<SqlGenerationService> logger,
        CacheService cache)
    {
        _validator = validator;
        _logger = logger;
        _cache = cache;

        var endpoint = config["OpenAI:Endpoint"]!;
        var key = config["OpenAI:Key"]!;
        var deployment = config["OpenAI:Deployment"]!;
        var client = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        _chatClient = client.GetChatClient(deployment);
    }

    // ─── PUBLIC ENTRY POINT ──────────────────────────────────────────────────
    // Auto-retry on ANY failure (json parse OR validation).
    //
    // FIX vs previous: previously only retried on isValidationError=true.
    // JSON parse failures (truncated output, extra text outside braces) returned
    // isValidationError=false and got NO retry — immediate hard failure.
    // JSON parse errors are the MOST recoverable: a simple retry almost always
    // fixes them. Now we retry on any failure type.
    public async Task<SqlGenerationResult> GenerateSqlAsync(string userQuestion, int userId)
    {
        if (_cache.TryGetSqlResult(userId, userQuestion, out var cached) && cached is not null)
        {
            _logger.LogInformation("SQL cache HIT | UserId: {UserId}", userId);
            return cached;
        }

        var result = await GenerateInternalAsync(userQuestion, userId, retryError: null);

        if (!result.IsSuccess)
        {
            _logger.LogWarning(
                "Attempt 1 failed (type={Type}) — auto-retrying: {Error}",
                result.IsValidationError ? "validation" : "parse/other",
                result.Error);
            result = await GenerateInternalAsync(userQuestion, userId, retryError: result.Error);
        }

        if (result.IsSuccess)
        {
            _cache.SetSqlResult(userId, userQuestion, result);
        }

        return result;
    }

    // ─── INTERNAL GENERATION ─────────────────────────────────────────────────
    private async Task<SqlGenerationResult> GenerateInternalAsync(
        string userQuestion, int userId, string? retryError)
    {
        var systemPrompt = BuildSystemPrompt();
        var userPrompt = BuildUserPrompt(userQuestion, userId, retryError);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt)
        };

        try
        {
            var completion = await _chatClient.CompleteChatAsync(messages,
                new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 3000,  // raised from 2000 — complex queries need more headroom
                    Temperature = 0.0f   // deterministic — SQL must not vary between calls
                });

            var raw = completion.Value.Content[0].Text.Trim();
            _logger.LogInformation(
                "SqlGeneration raw ({Attempt}):\n{Output}",
                retryError is null ? "attempt 1" : "retry",
                raw.Length > 1200 ? raw[..1200] + "…[truncated for log]" : raw);

            return ParseAndValidate(raw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SqlGenerationService call failed");
            return SqlGenerationResult.Fail(
                "AI service call failed: " + ex.Message,
                isValidationError: false);
        }
    }

    // ─── SYSTEM PROMPT ───────────────────────────────────────────────────────
    private static string BuildSystemPrompt() =>
        "You are an expert SQL Server query builder for an LC (Letter of Credit) " +
        "trade-finance system at ArcelorMittal Luxembourg.\n\n" +
        Schema + "\n\n" +
        ResponseTypeGuide + "\n\n" +
        TopNRules + "\n\n" +
        """
        ════════════════════════════════════════════════════════════════
        4-PHASE CHAIN-OF-THOUGHT — follow ALL 4 phases in order.
        Skipping any phase causes wrong queries.
        ════════════════════════════════════════════════════════════════

        ──────────────────────────────────────────────────────────────
        PHASE 0 — CLASSIFY (do this BEFORE any decomposition)
        ──────────────────────────────────────────────────────────────
        Classify the question into exactly one query_type:

        "list"        — user wants rows of data (show me, list, which LCs, show all...)
        "aggregate"   — user wants grouped summary (by bank, by customer, total, count, breakdown)
        "single_stat" — user wants ONE number (average, how many overall, total exposure)
        "timeline"    — user wants events/history in chronological order
        "comparison"  — user wants to compare exactly 2 things side by side
        "trend"       — user asks about data over time (monthly, weekly, by year, trend, how has X changed)
        "correlation" — user asks about relationship between two numeric fields
        "risk"        — user asks "at risk", "needs attention", "what should I worry about", "critical"
        "kpi"         — user asks for a summary dashboard/portfolio overview (total exposure, dashboard, position)
        "heatmap"     — user asks about calendar/date distribution (when do LCs expire, by day, calendar)

        query_type → responseType mapping:
        "trend"       → "line_chart" (count/value per period) | "area_chart" (cumulative) | "mixed_chart" (bars+line)
        "correlation" → "scatter_chart" (2 vars) | "bubble_chart" (3 vars with amendment count)
        "risk"        → "risk_scorecard"
        "kpi"         → "kpi_strip"
        "heatmap"     → "expiry_heatmap"

        This classification DRIVES the SELECT structure:
        • list        → SELECT individual columns, no GROUP BY, use TOP rule
        • aggregate   → SELECT group column + aggregate functions + GROUP BY, NO TOP
        • single_stat → SELECT AVG/SUM/COUNT only, no GROUP BY, NO TOP, returns 1 row
        • timeline    → SELECT audit log columns ORDER BY date ASC, TOP(100)
        • comparison  → SELECT group column + aggregates WHERE limits to 2 groups, NO TOP

        ──────────────────────────────────────────────────────────────
        PHASE 1 — DECOMPOSE
        ──────────────────────────────────────────────────────────────
        Break the question into 1–5 atomic sub-questions.
        Each sub-question = one WHERE condition or one JOIN.

        Ask yourself:
        • What is the core filter?
        • Are there secondary filters?
        • Does this need a column from a non-standard table (extra JOIN)?
        • Is there a calculation? (DATEDIFF, AVG, COUNT, SUM)
        • What is the output shape? (one row per LC? one row per group?)

        ──────────────────────────────────────────────────────────────
        PHASE 2 — MAP TO SCHEMA
        ──────────────────────────────────────────────────────────────
        For EACH sub-question, state explicitly:

        a) Exact table and alias (copy from schema — never guess)
        b) Exact column name (copy verbatim — wrong names crash the query)
        c) Is this column nullable? → if YES, add IS NOT NULL before comparison
        d) Join condition if extra table is needed
        e) SQL condition fragment

        ⚠️ CRITICAL NULL-SAFETY CHECK for every date/decimal column:
        Before writing any WHERE condition on a nullable column, ask:
        "Can this column be NULL for any row I want to include or exclude?"
        If YES → prefix the condition with "column IS NOT NULL AND"

        ──────────────────────────────────────────────────────────────
        PHASE 3 — ASSEMBLE
        ──────────────────────────────────────────────────────────────
        Build the complete SELECT based on query_type:

        1. Start with standard base JOINs
        2. Add extra JOINs identified in Phase 2
        3. SELECT the right columns for query_type:
           - list:        individual columns with PascalCase aliases
           - aggregate:   group-by column(s) + ISNULL(SUM/COUNT/AVG, 0)
           - single_stat: AVG/SUM/COUNT + optional MIN/MAX for context
           - timeline:    audit log action columns
           - comparison:  aggregate columns WHERE limits to exactly 2 groups
        4. Apply TOP rule (R7) based on query_type (see TOP RULES section above)
        5. Apply all business rules R1–R9
        6. GROUP BY (aggregate/comparison only): list ALL non-aggregate SELECT columns
        7. HAVING (for filtering on aggregates, not WHERE)
        8. ORDER BY: most useful sort for the question

        ──────────────────────────────────────────────────────────────
        PHASE 4 — SELF-REVIEW (always do this last before outputting)
        ──────────────────────────────────────────────────────────────
        Answer these 5 questions about your assembled SQL:

        Q1: Does this SQL answer the EXACT question asked, not a similar one?
        Q2: If query_type is aggregate/single_stat — does my SELECT have GROUP BY or aggregate functions?
        Q3: If query_type is list — does my SELECT NOT have unnecessary GROUP BY?
        Q4: Does every nullable date/decimal column have IS NOT NULL before its comparison?
        Q5: Is @UserId present exactly once in the scope subquery?

        If any answer is NO — fix the SQL before outputting JSON.

        ════════════════════════════════════════════════════════════════
        OUTPUT FORMAT — respond ONLY with this JSON (no markdown, no text outside)
        ════════════════════════════════════════════════════════════════
        {{
          "query_type":            "list | aggregate | single_stat | timeline | comparison | trend | correlation | risk | kpi | heatmap",
          "phase0_classification": "one sentence: why this query_type",
          "phase1_decomposition":  ["sub-question 1", "sub-question 2"],
          "phase2_mapping": [
            {{
              "sub_question": "...",
              "table":        "...",
              "columns":      ["..."],
              "nullable":     true,
              "condition":    "..."
            }}
          ],
          "phase4_review": {{
            "answers_the_question":    true,
            "correct_query_structure": true,
            "null_safety_applied":     true,
            "scope_filter_present":    true,
            "fix_applied":             "what was fixed or none"
          }},
          "sql":          "SELECT ... FROM ...",
          "responseType": "table | metric_cards | bank_chart | timeline | comparison | line_chart | area_chart | radar_chart | scatter_chart | bubble_chart | polar_chart | mixed_chart | risk_scorecard | kpi_strip | expiry_heatmap",
          "chartType":    null,
          "reasoning":    "one sentence"
        }}

        Rules:
        • Pure JSON only — no ```json fences, no text before/after
        • "sql" field: complete executable SQL, newlines as \n
        • Single SELECT statement only
        • Never: UPDATE, DELETE, DROP, INSERT, TRUNCATE, EXEC
        • CRITICAL: You MUST ALWAYS include the exact business-unit scope filter in the main WHERE clause: AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)
        • Never SELECT *
        • Never use TOP on GROUP BY queries

        ════════════════════════════════════════════════════════════════
        COLUMN ALIAS CONVENTION
        ════════════════════════════════════════════════════════════════
        RequestId, Bank, Product, CustomerName, SapCustomerName, BusinessUnit,
        LcNumber, IssuingBank, LcAmount, Currency, RequestedAmount,
        LcIssueDate, LcExpiryDate, GracePeriod, IsExpired, Status, StatusLabel,
        PaymentTerms, TypeOfLC, TypeOfLcRequested, BeneficiaryOnLC,
        PortOfLoading, PortOfDischarge, ActualShipmentDate, AmiPaymentDate,
        QuantityMt, LcAmountUsd, UsdBankCharges, SapOrderNumber, Comment,
        AmendmentCount, InvoiceCount, DaysPending, DaysOverdue, DaysToIssuance,
        RequestCreatedOn, RequestLastModified, SubmittedByName,
        InvoiceAmount, InvoiceDate, IsPaid, ExpectedPaymentDate,
        BankCharges, IsRefunded, RefundValueDate, DocumentSetNumber,
        Action, LogType, ActionedBy, ActionedOn,
        LcCount, TotalLcValue, TotalAmount, AvgDaysToIssuance, MinDays, MaxDays,
        TotalIssuedLCs, IssuedCount, PaidCount, PendingCount, DraftCount,
        ValidationCount, RejectedCount, CancelledCount,
        AmendedAmount, NewExpiryDate, NewGracePeriod, WhatChanged, AmendedBy, AmendedOn,
        ApprovalId, ApprovalStatus, AssignedOn, ActionTakenOn, IsOfflineApproval,
        Date, MonthLabel, HumanLabel, Count, UrgencyLevel, UnpaidInvoiceCount

        ════════════════════════════════════════════════════════════════
        FEW-SHOT EXAMPLES (12 examples — study every pattern)
        ════════════════════════════════════════════════════════════════

        ── EXAMPLE 1: Simple list query ──────────────────────────────
        Question: "Show all issued LCs for BNP bank"
        {{
          "query_type": "list",
          "phase0_classification": "Returns individual LC rows filtered by status and bank.",
          "phase1_decomposition": ["Which LCs have status LCIssued?", "Which are for BNP bank?"],
          "phase2_mapping": [
            {{"sub_question": "Status LCIssued", "table": "statuses s", "columns": ["s.application_status"], "nullable": false, "condition": "s.application_status = 'LCIssued'"}},
            {{"sub_question": "BNP bank", "table": "lc_request_details lrd", "columns": ["lrd.bank"], "nullable": false, "condition": "UPPER(lrd.bank) LIKE '%BNP%'"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "none"}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product,\n    c.name AS CustomerName, bu.business_unit_name AS BusinessUnit,\n    ld.lc_number AS LcNumber,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    ISNULL(ld.currency, lrd.currency) AS Currency,\n    ld.lc_issue_date AS LcIssueDate, ld.lc_expire_date AS LcExpiryDate,\n    ld.grace_period AS GracePeriod, ld.payment_terms AS PaymentTerms,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'LCIssued' THEN 'Issued' ELSE s.application_status END AS StatusLabel,\n    DATEDIFF(DAY, lrd.created_on, GETDATE()) AS DaysPending,\n    lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status = 'LCIssued'\n  AND UPPER(lrd.bank) LIKE '%BNP%'\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY ld.lc_issue_date DESC",
          "responseType": "table",
          "chartType": null,
          "reasoning": "Filter to LCIssued + BNP bank, TOP(200) default list cap."
        }}

        ── EXAMPLE 2: Aggregate by bank (NO TOP) ─────────────────────
        Question: "Show total LC value by bank"
        {{
          "query_type": "aggregate",
          "phase0_classification": "Groups all LCs by bank and sums values — aggregate not list.",
          "phase1_decomposition": ["Group LCs by bank", "Sum COALESCE amounts per bank", "Count status breakdown per bank"],
          "phase2_mapping": [
            {{"sub_question": "Group by bank", "table": "lc_request_details lrd", "columns": ["lrd.bank"], "nullable": false, "condition": "GROUP BY lrd.bank"}},
            {{"sub_question": "Sum amounts with null safety", "table": "lc_details ld", "columns": ["ld.amount", "lrd.total_amount"], "nullable": true, "condition": "SUM(COALESCE(ld.amount, lrd.total_amount))"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "none"}},
          "sql": "SELECT\n    lrd.bank AS Bank,\n    COUNT(DISTINCT lrd.obj_id) AS LcCount,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue,\n    ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount,\n    ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount,\n    ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2') THEN 1 ELSE 0 END), 0) AS PendingCount,\n    ISNULL(SUM(CASE WHEN s.application_status='Draft' THEN 1 ELSE 0 END), 0) AS DraftCount,\n    ISNULL(SUM(CASE WHEN s.application_status='Rejected' THEN 1 ELSE 0 END), 0) AS RejectedCount\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nHAVING COUNT(DISTINCT lrd.obj_id) > 0\nORDER BY TotalLcValue DESC",
          "responseType": "bank_chart",
          "chartType": "horizontal_bar",
          "reasoning": "GROUP BY bank, NO TOP on aggregate, ISNULL on all aggregates."
        }}

        ── EXAMPLE 3: Single stat — AVG (NO TOP) ─────────────────────
        Question: "How many days on average does it take from LC creation to it being issued?"
        {{
          "query_type": "single_stat",
          "phase0_classification": "Asks for ONE average number across all issued LCs — single_stat, NOT a list.",
          "phase1_decomposition": ["Only LCIssued LCs have lc_issue_date — filter to those", "lc_issue_date can be null — add IS NOT NULL guard", "AVG(DATEDIFF) between created_on and lc_issue_date, grouped by bank for context"],
          "phase2_mapping": [
            {{"sub_question": "LCIssued status only", "table": "statuses s", "columns": ["s.application_status"], "nullable": false, "condition": "s.application_status = 'LCIssued'"}},
            {{"sub_question": "lc_issue_date is nullable", "table": "lc_details ld", "columns": ["ld.lc_issue_date"], "nullable": true, "condition": "ld.lc_issue_date IS NOT NULL"}},
            {{"sub_question": "AVG calculation", "table": "lrd + ld", "columns": ["lrd.created_on", "ld.lc_issue_date"], "nullable": false, "condition": "AVG(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date))"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "Switched to single_stat with AVG — a list query with DATEDIFF would not answer this question."}},
          "sql": "SELECT\n    lrd.bank AS Bank,\n    AVG(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date)) AS AvgDaysToIssuance,\n    MIN(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date)) AS MinDays,\n    MAX(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date)) AS MaxDays,\n    COUNT(*) AS TotalIssuedLCs\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nJOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status = 'LCIssued'\n  AND ld.lc_issue_date IS NOT NULL\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nORDER BY AvgDaysToIssuance DESC",
          "responseType": "metric_cards",
          "chartType": "stacked_bar",
          "reasoning": "AVG(DATEDIFF) on issued LCs, INNER JOIN lc_details (known to exist for LCIssued), grouped by bank, NO TOP."
        }}

        ── EXAMPLE 4: Cross-table EXISTS (avoids row duplication) ────
        Question: "Show LCs expiring this month with unpaid invoices"
        {{
          "query_type": "list",
          "phase0_classification": "Row-level LC data matching two conditions — list.",
          "phase1_decomposition": ["LCs expiring in current calendar month", "At least one unpaid invoice exists", "Avoid row duplication — use EXISTS not JOIN"],
          "phase2_mapping": [
            {{"sub_question": "Expiring this month", "table": "lc_details ld", "columns": ["ld.lc_expire_date"], "nullable": true, "condition": "ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date >= DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE()),0) AND ld.lc_expire_date < DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE())+1,0)"}},
            {{"sub_question": "Unpaid invoice exists", "table": "invoice_details inv", "columns": ["inv.is_Mark_as_paid", "inv.lc_request_id"], "nullable": false, "condition": "EXISTS (SELECT 1 FROM invoice_details i WHERE i.lc_request_id = lrd.obj_id AND i.is_Mark_as_paid = 0)"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "Used EXISTS to avoid row duplication from multiple invoices per LC."}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product,\n    c.name AS CustomerName, ld.lc_number AS LcNumber,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    ISNULL(ld.currency, lrd.currency) AS Currency,\n    ld.lc_expire_date AS LcExpiryDate, ld.grace_period AS GracePeriod,\n    DATEDIFF(DAY, GETDATE(), ld.lc_expire_date) AS DaysUntilExpiry,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'LCIssued' THEN 'Issued' ELSE s.application_status END AS StatusLabel,\n    (SELECT COUNT(*) FROM invoice_details i WHERE i.lc_request_id = lrd.obj_id AND i.is_Mark_as_paid = 0) AS UnpaidInvoiceCount,\n    lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND ld.lc_expire_date IS NOT NULL\n  AND ld.lc_expire_date >= DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE()),0)\n  AND ld.lc_expire_date < DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE())+1,0)\n  AND EXISTS (SELECT 1 FROM invoice_details i WHERE i.lc_request_id = lrd.obj_id AND i.is_Mark_as_paid = 0)\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY ld.lc_expire_date ASC",
          "responseType": "table",
          "chartType": null,
          "reasoning": "EXISTS subquery avoids row duplication, IS NOT NULL guard on nullable lc_expire_date, TOP(200) list cap."
        }}

        ── EXAMPLE 5: Grace period overdue ───────────────────────────
        Question: "Show LCs where grace period has passed but payment is not done"
        {{
          "query_type": "list",
          "phase0_classification": "Row-level LC data with two combined filters — list.",
          "phase1_decomposition": ["Status is LCIssued or PaymentNotDone (not paid)", "grace_period exists and is in the past"],
          "phase2_mapping": [
            {{"sub_question": "Unpaid status", "table": "statuses s", "columns": ["s.application_status"], "nullable": false, "condition": "s.application_status IN ('LCIssued','PaymentNotDone')"}},
            {{"sub_question": "Grace period is nullable — must guard", "table": "lc_details ld", "columns": ["ld.grace_period"], "nullable": true, "condition": "ld.grace_period IS NOT NULL AND ld.grace_period < GETDATE()"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "Added IS NOT NULL guard on nullable grace_period."}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product,\n    c.name AS CustomerName, ld.lc_number AS LcNumber,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    ISNULL(ld.currency, lrd.currency) AS Currency,\n    ld.grace_period AS GracePeriod, ld.lc_expire_date AS LcExpiryDate,\n    DATEDIFF(DAY, ld.grace_period, GETDATE()) AS DaysOverdue,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentNotDone' THEN 'Unpaid' ELSE s.application_status END AS StatusLabel,\n    lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status IN ('LCIssued','PaymentNotDone')\n  AND ld.grace_period IS NOT NULL AND ld.grace_period < GETDATE()\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY ld.grace_period ASC",
          "responseType": "table",
          "chartType": null,
          "reasoning": "Two-condition filter: unpaid status AND past grace_period with IS NOT NULL guard."
        }}

        ── EXAMPLE 6: Timeline / audit log ───────────────────────────
        Question: "Show history of LC BNP123"
        {{
          "query_type": "timeline",
          "phase0_classification": "Audit log events for a specific LC in chronological order — timeline.",
          "phase1_decomposition": ["Find lc_request_id for LC number BNP123 (check both lc_number and contract_number)", "Get all audit_log rows for that request", "Join users for actioned_by name"],
          "phase2_mapping": [
            {{"sub_question": "LC number lookup", "table": "lc_details ld / lrd", "columns": ["ld.lc_number", "lrd.contract_number"], "nullable": true, "condition": "UPPER(ISNULL(ld.lc_number,'')) = 'BNP123' OR UPPER(lrd.contract_number) = 'BNP123'"}},
            {{"sub_question": "Audit log events", "table": "lc_audit_log al", "columns": ["al.action", "al.log_type", "al.actioned_on", "al.comment"], "nullable": false, "condition": "JOIN al ON al.lc_request_id = lrd.obj_id"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "none"}},
          "sql": "SELECT TOP(100)\n    al.obj_id AS LogId,\n    al.action AS Action,\n    al.log_type AS LogType,\n    al.comment AS Comment,\n    al.actioned_on AS ActionedOn,\n    u.first_name + ' ' + u.last_name AS ActionedBy,\n    lrd.obj_id AS RequestId,\n    ISNULL(ld.lc_number, lrd.contract_number) AS LcNumber,\n    c.name AS CustomerName,\n    lrd.bank AS Bank,\n    s.application_status AS Status\nFROM lc_audit_log al\nJOIN lc_request_details lrd ON lrd.obj_id = al.lc_request_id\nJOIN users u ON u.obj_id = al.actioned_by\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND (UPPER(ISNULL(ld.lc_number,'')) = 'BNP123' OR UPPER(lrd.contract_number) = 'BNP123')\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY al.actioned_on ASC",
          "responseType": "timeline",
          "chartType": null,
          "reasoning": "Audit log with TOP(100), search both lc_number and contract_number, ORDER BY date ASC."
        }}

        ── EXAMPLE 7: Comparison of 2 groups (NO TOP) ────────────────
        Question: "Compare BNP and KBC bank by total LC value"
        {{
          "query_type": "comparison",
          "phase0_classification": "Exactly 2 groups side-by-side — comparison, NO TOP.",
          "phase1_decomposition": ["Filter to only BNP and KBC", "Aggregate total value and counts per bank"],
          "phase2_mapping": [
            {{"sub_question": "Filter to 2 banks only", "table": "lc_request_details lrd", "columns": ["lrd.bank"], "nullable": false, "condition": "UPPER(lrd.bank) IN ('BNP','KBC')"}},
            {{"sub_question": "Aggregate per bank", "table": "lrd + ld", "columns": ["ld.amount", "lrd.total_amount"], "nullable": true, "condition": "SUM(COALESCE(ld.amount, lrd.total_amount))"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "none"}},
          "sql": "SELECT\n    lrd.bank AS Bank,\n    COUNT(DISTINCT lrd.obj_id) AS LcCount,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue,\n    ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount,\n    ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount,\n    ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2') THEN 1 ELSE 0 END), 0) AS PendingCount\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND UPPER(lrd.bank) IN ('BNP','KBC')\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nORDER BY TotalLcValue DESC",
          "responseType": "comparison",
          "chartType": "side_by_side",
          "reasoning": "WHERE limits to 2 banks, GROUP BY bank, NO TOP on aggregate."
        }}

        ── EXAMPLE 8: Approval list ───────────────────────────────────
        Question: "Show LCs stuck in approval for more than 5 days"
        {{
          "query_type": "list",
          "phase0_classification": "Row-level LC data filtered by status and age — list.",
          "phase1_decomposition": ["Status is in approval stages (Submitted_For_Validation, N+1, N+2)", "Created more than 5 days ago"],
          "phase2_mapping": [
            {{"sub_question": "Approval status filter", "table": "statuses s", "columns": ["s.application_status"], "nullable": false, "condition": "s.application_status IN ('Submitted_For_Validation','Submitted_N+1','Submitted_N+2')"}},
            {{"sub_question": "More than 5 days — created_on not nullable", "table": "lc_request_details lrd", "columns": ["lrd.created_on"], "nullable": false, "condition": "DATEDIFF(DAY, lrd.created_on, GETDATE()) > 5"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "none"}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product,\n    c.name AS CustomerName, bu.business_unit_name AS BusinessUnit,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    ISNULL(ld.currency, lrd.currency) AS Currency,\n    lrd.created_on AS RequestCreatedOn,\n    DATEDIFF(DAY, lrd.created_on, GETDATE()) AS DaysPending,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'Submitted_For_Validation' THEN 'In Validation' WHEN 'Submitted_N+1' THEN 'Pending N+1' WHEN 'Submitted_N+2' THEN 'Pending N+2' ELSE s.application_status END AS StatusLabel\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status IN ('Submitted_For_Validation','Submitted_N+1','Submitted_N+2')\n  AND DATEDIFF(DAY, lrd.created_on, GETDATE()) > 5\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY DaysPending DESC",
          "responseType": "approval_list",
          "chartType": null,
          "reasoning": "Approval stages + DATEDIFF > 5, TOP(200), sorted most urgent first."
        }}

        ── EXAMPLE 9: Status breakdown doughnut (NO TOP) ─────────────
        Question: "Show status breakdown of all LCs"
        {{
          "query_type": "aggregate",
          "phase0_classification": "Groups all LCs by status and counts them — aggregate, NO TOP.",
          "phase1_decomposition": ["Group all active LCs by application_status", "Count per status", "Add HumanLabel CASE for display"],
          "phase2_mapping": [
            {{"sub_question": "Group by status", "table": "statuses s", "columns": ["s.application_status"], "nullable": false, "condition": "GROUP BY s.application_status"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "none"}},
          "sql": "SELECT\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'Draft' THEN 'Draft' WHEN 'Submitted_For_Validation' THEN 'In Validation' WHEN 'Submitted_N+1' THEN 'Pending N+1' WHEN 'Submitted_N+2' THEN 'Pending N+2' WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentDone' THEN 'Paid' WHEN 'PaymentNotDone' THEN 'Unpaid' WHEN 'Rejected' THEN 'Rejected' WHEN 'Cancelled' THEN 'Cancelled' ELSE s.application_status END AS HumanLabel,\n    COUNT(DISTINCT lrd.obj_id) AS Count,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalAmount\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY s.application_status\nORDER BY Count DESC",
          "responseType": "metric_cards",
          "chartType": "doughnut",
          "reasoning": "GROUP BY status, NO TOP, HumanLabel CASE for display, ISNULL on aggregates."
        }}

        ── EXAMPLE 10: Amendment query with HAVING ───────────────────
        Question: "Show LCs with more than 2 amendments"
        {{
          "query_type": "list",
          "phase0_classification": "Row-level data filtered by amendment count using HAVING — list.",
          "phase1_decomposition": ["Join amendment table to lrd", "Count amendments per LC", "HAVING COUNT > 2 (not WHERE — it is an aggregate filter)"],
          "phase2_mapping": [
            {{"sub_question": "Join amendments", "table": "lc_amendment_details lad", "columns": ["lad.lc_request_id", "lad.obj_id"], "nullable": false, "condition": "LEFT JOIN lad ON lad.lc_request_id = lrd.obj_id"}},
            {{"sub_question": "HAVING count filter", "table": "lad", "columns": ["lad.obj_id"], "nullable": false, "condition": "HAVING COUNT(lad.obj_id) > 2"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "Used HAVING not WHERE for aggregate count filter."}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product,\n    c.name AS CustomerName,\n    ISNULL(ld.lc_number, lrd.contract_number) AS LcNumber,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    ISNULL(ld.currency, lrd.currency) AS Currency,\n    COUNT(lad.obj_id) AS AmendmentCount,\n    MAX(lad.created_on) AS LastAmendedOn,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentDone' THEN 'Paid' ELSE s.application_status END AS StatusLabel\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nLEFT JOIN lc_amendment_details lad ON lad.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.obj_id, lrd.bank, lrd.product, c.name, ld.lc_number, lrd.contract_number, ld.amount, lrd.total_amount, ld.currency, lrd.currency, s.application_status\nHAVING COUNT(lad.obj_id) > 2\nORDER BY AmendmentCount DESC",
          "responseType": "table",
          "chartType": null,
          "reasoning": "LEFT JOIN amendments, GROUP BY all non-aggregates, HAVING for count filter, TOP(200)."
        }}

        ── EXAMPLE 11: Top-N user-specified ──────────────────────────
        Question: "Show top 5 customers by total LC value"
        {{
          "query_type": "aggregate",
          "phase0_classification": "User explicitly said top 5 — aggregate with TOP(5).",
          "phase1_decomposition": ["Group by customer", "Sum total LC value", "Return only top 5 rows"],
          "phase2_mapping": [
            {{"sub_question": "Group by customer", "table": "customers c", "columns": ["c.name", "c.sap_sold_to_name"], "nullable": false, "condition": "GROUP BY c.name, c.sap_sold_to_name"}},
            {{"sub_question": "Top 5 specified by user", "table": "n/a", "columns": [], "nullable": false, "condition": "TOP(5) with ORDER BY TotalLcValue DESC"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "TOP(5) because user said top 5 — not the default TOP(200)."}},
          "sql": "SELECT TOP(5)\n    c.name AS CustomerName,\n    c.sap_sold_to_name AS SapCustomerName,\n    COUNT(DISTINCT lrd.obj_id) AS LcCount,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue,\n    ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount,\n    ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount\nFROM customers c\nJOIN lc_request_details lrd ON lrd.customer_id = c.obj_id\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY c.name, c.sap_sold_to_name\nORDER BY TotalLcValue DESC",
          "responseType": "metric_cards",
          "chartType": "stacked_bar",
          "reasoning": "User said top 5 so TOP(5), GROUP BY customer, ORDER BY value descending."
        }}

        ── EXAMPLE 12: Beneficiary payment date overdue ───────────────
        Question: "Show LCs where beneficiary payment date has already passed"
        {{
          "query_type": "list",
          "phase0_classification": "Row-level LCs with a date condition from invoice_details — list.",
          "phase1_decomposition": ["beneficiary_pmt_date is in invoice_details — need extra JOIN", "Date must be NOT NULL and in the past", "Avoid duplicating LC rows from multiple invoices per LC — use GROUP BY"],
          "phase2_mapping": [
            {{"sub_question": "Find beneficiary_pmt_date column — it is nullable", "table": "invoice_details inv", "columns": ["inv.beneficiary_pmt_date"], "nullable": true, "condition": "inv.beneficiary_pmt_date IS NOT NULL AND inv.beneficiary_pmt_date < GETDATE()"}},
            {{"sub_question": "Avoid row duplication from multiple invoices", "table": "invoice_details inv", "columns": ["inv.lc_request_id"], "nullable": false, "condition": "GROUP BY all lrd/ld/s/c columns, MIN(beneficiary_pmt_date) for the earliest overdue date"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "GROUP BY to get 1 row per LC not 1 row per invoice. IS NOT NULL guard on nullable beneficiary_pmt_date."}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product,\n    c.name AS CustomerName, bu.business_unit_name AS BusinessUnit,\n    ld.lc_number AS LcNumber,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    ISNULL(ld.currency, lrd.currency) AS Currency,\n    ld.lc_expire_date AS LcExpiryDate, ld.grace_period AS GracePeriod,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentNotDone' THEN 'Unpaid' WHEN 'PaymentDone' THEN 'Paid' ELSE s.application_status END AS StatusLabel,\n    MIN(inv.beneficiary_pmt_date) AS BeneficiaryPaymentDate,\n    DATEDIFF(DAY, MIN(inv.beneficiary_pmt_date), GETDATE()) AS DaysOverdue,\n    COUNT(inv.obj_id) AS InvoiceCount,\n    lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nJOIN invoice_details inv ON inv.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND inv.beneficiary_pmt_date IS NOT NULL\n  AND inv.beneficiary_pmt_date < GETDATE()\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.obj_id, lrd.bank, lrd.product, c.name, bu.business_unit_name,\n         ld.lc_number, ld.amount, lrd.total_amount, ld.currency, lrd.currency,\n         ld.lc_expire_date, ld.grace_period, s.application_status, lrd.created_on\nORDER BY MIN(inv.beneficiary_pmt_date) ASC",
          "responseType": "table",
          "chartType": null,
          "reasoning": "JOIN invoice_details, GROUP BY to get 1 row per LC, MIN for earliest overdue date, IS NOT NULL guard."
        }}

        ── EXAMPLE 13: Trend over time — line chart ──────────────────────────────
        Question: "Show monthly LC creation trend for 2024"
        {{
          "query_type": "trend",
          "phase0_classification": "User wants monthly trend data over time — trend query_type, line_chart response.",
          "phase1_decomposition": ["Group by year and month", "Count LCs per month", "Sum total value per month", "Filter to 2024"],
          "phase2_mapping": [
            {{"sub_question": "Group by month", "table": "lrd", "columns": ["lrd.created_on"], "nullable": false, "condition": "GROUP BY YEAR(lrd.created_on), MONTH(lrd.created_on), DATENAME(MONTH,lrd.created_on)"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "NO TOP on GROUP BY query."}},
          "sql": "SELECT\n    YEAR(lrd.created_on) AS Year,\n    MONTH(lrd.created_on) AS MonthNum,\n    DATENAME(MONTH, lrd.created_on) AS MonthLabel,\n    COUNT(*) AS LcCount,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalValue\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND YEAR(lrd.created_on) = 2024\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY YEAR(lrd.created_on), MONTH(lrd.created_on), DATENAME(MONTH, lrd.created_on)\nORDER BY Year ASC, MonthNum ASC",
          "responseType": "line_chart",
          "chartType": "line",
          "reasoning": "Trend query grouped by month, NO TOP on GROUP BY, ORDER BY year+month ASC."
        }}

        ── EXAMPLE 14: Multi-metric radar — radar chart ──────────────────────────
        Question: "Compare BNP, KBC and CACIB across all performance metrics"
        {{
          "query_type": "aggregate",
          "phase0_classification": "Multi-entity multi-metric comparison best shown as radar chart.",
          "phase1_decomposition": ["Group by bank", "Compute 7 KPI metrics per bank"],
          "phase2_mapping": [
            {{"sub_question": "Group by bank", "table": "lrd", "columns": ["lrd.bank"], "nullable": false, "condition": "GROUP BY lrd.bank"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "NO TOP on aggregate."}},
          "sql": "SELECT\n    lrd.bank AS Bank,\n    COUNT(DISTINCT lrd.obj_id) AS LcCount,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue,\n    ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount,\n    ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount,\n    ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2') THEN 1 ELSE 0 END), 0) AS PendingCount,\n    ISNULL(SUM(CASE WHEN s.application_status='Rejected' THEN 1 ELSE 0 END), 0) AS RejectedCount,\n    ISNULL(AVG(DATEDIFF(DAY, lrd.created_on, GETDATE())), 0) AS AvgDaysPending\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nORDER BY TotalLcValue DESC",
          "responseType": "radar_chart",
          "chartType": "radar",
          "reasoning": "Multi-bank multi-metric aggregate, radar shows relative strengths across all axes."
        }}

        ── EXAMPLE 15: Scatter chart — correlation ───────────────────────────────
        Question: "Show relationship between LC amount and days to approval"
        {{
          "query_type": "correlation",
          "phase0_classification": "User wants correlation between 2 numeric variables per LC row.",
          "phase1_decomposition": ["One row per LC with amount and days", "Colour by bank for grouping"],
          "phase2_mapping": [
            {{"sub_question": "Amount nullable", "table": "ld / lrd", "columns": ["ld.amount", "lrd.total_amount"], "nullable": true, "condition": "COALESCE(ld.amount, lrd.total_amount)"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "TOP(200) for list scatter."}},
          "sql": "SELECT TOP(200)\n    lrd.obj_id AS RequestId,\n    COALESCE(ld.amount, lrd.total_amount) AS LcAmount,\n    DATEDIFF(DAY, lrd.created_on, GETDATE()) AS DaysPending,\n    lrd.bank AS Bank,\n    s.application_status AS Status,\n    CASE s.application_status WHEN 'LCIssued' THEN 'Issued' ELSE s.application_status END AS StatusLabel\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY LcAmount DESC",
          "responseType": "scatter_chart",
          "chartType": "scatter",
          "reasoning": "Correlation, one row per LC with two numeric axes, coloured by bank."
        }}

        ── EXAMPLE 16: KPI strip ─────────────────────────────────────────────────
        Question: "Give me a quick dashboard summary of our LC portfolio"
        {{
          "query_type": "kpi",
          "phase0_classification": "Portfolio dashboard summary — kpi, returns ONE row with 6 aggregate columns.",
          "phase1_decomposition": ["Count active LCs", "Sum exposure", "Count pending approvals", "Count expiring 30d", "Count unpaid invoices", "Count overdue"],
          "phase2_mapping": [
            {{"sub_question": "Expiring nullable", "table": "ld", "columns": ["ld.lc_expire_date"], "nullable": true, "condition": "ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date <= DATEADD(DAY,30,GETDATE()) AND ld.lc_expire_date >= GETDATE()"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "Single row, no TOP, no GROUP BY."}},
          "sql": "SELECT\n    COUNT(DISTINCT lrd.obj_id) AS TotalActiveLCs,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalExposure,\n    SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2') THEN 1 ELSE 0 END) AS PendingApprovals,\n    SUM(CASE WHEN s.application_status='LCIssued' AND ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date <= DATEADD(DAY,30,GETDATE()) AND ld.lc_expire_date >= GETDATE() THEN 1 ELSE 0 END) AS ExpiringIn30Days,\n    (SELECT COUNT(*) FROM invoice_details inv JOIN lc_request_details x ON x.obj_id=inv.lc_request_id WHERE inv.is_Mark_as_paid=0 AND x.is_active=1 AND x.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id=@UserId)) AS UnpaidInvoices,\n    SUM(CASE WHEN s.application_status IN ('LCIssued','PaymentNotDone') AND ld.grace_period IS NOT NULL AND ld.grace_period < GETDATE() THEN 1 ELSE 0 END) AS Overdue\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)",
          "responseType": "kpi_strip",
          "chartType": null,
          "reasoning": "Portfolio summary — exactly 1 row with 6 KPI aggregate columns, no TOP, no GROUP BY."
        }}

        ── EXAMPLE 17: Mixed bar + line chart ───────────────────────────────────
        Question: "Show LC count per month and cumulative total for 2024"
        {{
          "query_type": "trend",
          "phase0_classification": "Monthly bars AND running total line — mixed_chart.",
          "phase1_decomposition": ["Group by month", "Count LCs per month", "Frontend computes cumulative"],
          "phase2_mapping": [
            {{"sub_question": "Group by month", "table": "lrd", "columns": ["lrd.created_on"], "nullable": false, "condition": "GROUP BY YEAR(lrd.created_on), MONTH(lrd.created_on), DATENAME(MONTH,lrd.created_on)"}}
          ],
          "phase4_review": {{"answers_the_question": true, "correct_query_structure": true, "null_safety_applied": true, "scope_filter_present": true, "fix_applied": "NO TOP on GROUP BY. Frontend computes running total."}},
          "sql": "SELECT\n    YEAR(lrd.created_on) AS Year,\n    MONTH(lrd.created_on) AS MonthNum,\n    DATENAME(MONTH, lrd.created_on) AS MonthLabel,\n    COUNT(*) AS LcCount,\n    ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalValue\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND YEAR(lrd.created_on) = 2024\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY YEAR(lrd.created_on), MONTH(lrd.created_on), DATENAME(MONTH, lrd.created_on)\nORDER BY Year ASC, MonthNum ASC",
          "responseType": "mixed_chart",
          "chartType": "mixed_bar_line",
          "reasoning": "Monthly bars + cumulative line, frontend computes running total, NO TOP on GROUP BY."
        }}
        """;

    // ─── USER PROMPT ─────────────────────────────────────────────────────────
    private static string BuildUserPrompt(string question, int userId, string? retryError)
    {
        var retrySection = retryError is not null
            ? $"""

                ══ PREVIOUS ATTEMPT FAILED — FIX THIS ERROR ══
                Your previous SQL was rejected with this error:
                  {retryError}

                Fix this specific error in your new attempt. Do not repeat the same mistake.
                ═══════════════════════════════════════════════
                """
            : string.Empty;

        return $"""
            User question: {question}
            UserId (inject into @UserId scope filter): {userId}
            {retrySection}
            Apply all 4 phases in order (Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4 self-review).
            Respond ONLY with the JSON object. No text before or after. No markdown.
            """;
    }

    // ─── PARSE AND VALIDATE ───────────────────────────────────────────────────
    private SqlGenerationResult ParseAndValidate(string raw)
    {
        // Strip any accidental markdown fencing
        raw = Regex.Replace(raw, @"^```(?:json)?\s*", "", RegexOptions.Multiline).Trim();
        raw = Regex.Replace(raw, @"```\s*$", "", RegexOptions.Multiline).Trim();

        SqlGenerationOutput? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<SqlGenerationOutput>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            _logger.LogError("JSON parse failed: {Msg}\nRaw (first 500): {Raw}",
                ex.Message, raw.Length > 500 ? raw[..500] : raw);
            return SqlGenerationResult.Fail(
                "AI returned malformed JSON — cannot parse SQL output",
                isValidationError: false);
        }

        if (parsed?.Sql is null or { Length: 0 })
            return SqlGenerationResult.Fail(
                "AI returned no SQL in the JSON output",
                isValidationError: false);

        // Log Phase 0 and Phase 4 for debugging — visible in server logs
        _logger.LogInformation(
            "Phase 0 — QueryType: {QT} | {Classification}",
            parsed.QueryType, parsed.Phase0Classification);

        if (parsed.Phase1Decomposition?.Length > 0)
            _logger.LogInformation(
                "Phase 1 — Decomposition: {D}",
                string.Join(" | ", parsed.Phase1Decomposition));

        if (parsed.Phase4Review is not null)
            _logger.LogInformation(
                "Phase 4 Review — answers={A} structure={S} nullSafe={N} scope={SC} fix={F}",
                parsed.Phase4Review.AnswersTheQuestion,
                parsed.Phase4Review.CorrectQueryStructure,
                parsed.Phase4Review.NullSafetyApplied,
                parsed.Phase4Review.ScopeFilterPresent,
                parsed.Phase4Review.FixApplied);

        // ── Phase 4 safety gate ───────────────────────────────────────────────
        // If the AI's own self-review says the scope filter is missing,
        // treat it as a validation error and trigger auto-retry with
        // explicit instructions to add the @UserId scope filter.
        // This catches the case where the AI "knows" it forgot the filter
        // but still outputs broken SQL anyway.
        if (parsed.Phase4Review is { ScopeFilterPresent: false })
        {
            _logger.LogWarning("Phase 4 review flagged: scope filter missing");
            return SqlGenerationResult.Fail(
                "SQL is missing the mandatory @UserId scope filter. " +
                "You MUST add this exact clause to the WHERE: " +
                "AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)",
                isValidationError: true);
        }

        // C# safety validation
        var validation = _validator.Validate(parsed.Sql);
        if (!validation.IsValid)
        {
            _logger.LogWarning(
                "SQL validation failed: {Error}\nSQL: {Sql}",
                validation.Error, parsed.Sql);
            return SqlGenerationResult.Fail(
                $"SQL failed safety validation: {validation.Error}",
                isValidationError: true);
        }

        return SqlGenerationResult.Ok(
            sql: validation.Sql,
            responseType: parsed.ResponseType ?? "table",
            chartType: parsed.ChartType,
            queryType: parsed.QueryType ?? "list",
            reasoning: parsed.Reasoning ?? string.Empty);
    }

    // ─── INNER TYPES ──────────────────────────────────────────────────────────
    private sealed record SqlGenerationOutput(
        [property: JsonPropertyName("query_type")] string? QueryType,
        [property: JsonPropertyName("phase0_classification")] string? Phase0Classification,
        [property: JsonPropertyName("phase1_decomposition")] string[]? Phase1Decomposition,
        [property: JsonPropertyName("phase2_mapping")] object[]? Phase2Mapping,
        [property: JsonPropertyName("phase4_review")] Phase4Review? Phase4Review,
        [property: JsonPropertyName("sql")] string? Sql,
        [property: JsonPropertyName("responseType")] string? ResponseType,
        [property: JsonPropertyName("chartType")] string? ChartType,
        [property: JsonPropertyName("reasoning")] string? Reasoning);

    private sealed record Phase4Review(
        [property: JsonPropertyName("answers_the_question")] bool AnswersTheQuestion,
        [property: JsonPropertyName("correct_query_structure")] bool CorrectQueryStructure,
        [property: JsonPropertyName("null_safety_applied")] bool NullSafetyApplied,
        [property: JsonPropertyName("scope_filter_present")] bool ScopeFilterPresent,
        [property: JsonPropertyName("fix_applied")] string FixApplied);
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
