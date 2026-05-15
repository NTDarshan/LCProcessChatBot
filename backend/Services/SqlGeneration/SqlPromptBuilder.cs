namespace backend.Services.SqlGeneration;

/// <summary>
/// Constructs the OpenAI system prompt and user prompt from the three content providers.
/// Single responsibility: prompt assembly only. No AI calls, no parsing.
/// </summary>
public sealed class SqlPromptBuilder
{
    private readonly SchemaProvider _schema;
    private readonly QueryRuleProvider _rules;
    private readonly ResponseTypeGuideProvider _guide;

    public SqlPromptBuilder(
        SchemaProvider schema,
        QueryRuleProvider rules,
        ResponseTypeGuideProvider guide)
    {
        _schema = schema;
        _rules  = rules;
        _guide  = guide;
    }

    // ─── SYSTEM PROMPT ───────────────────────────────────────────────────────
    public string BuildSystemPrompt() =>
        "You are an expert SQL Server query builder for an LC (Letter of Credit) " +
        "trade-finance system at ArcelorMittal Luxembourg.\n\n" +
        _schema.GetSchema() + "\n\n" +
        _guide.GetGuide() + "\n\n" +
        _rules.GetRules() + "\n\n" +
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
        """;

    // ─── USER PROMPT ─────────────────────────────────────────────────────────
    public string BuildUserPrompt(string userQuestion, int userId, string? retryError)
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
            User question: {userQuestion}
            UserId (inject into @UserId scope filter): {userId}
            {retrySection}
            Apply all 4 phases in order (Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4 self-review).
            Respond ONLY with the JSON object. No text before or after. No markdown.
            """;
    }
}
