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

        ════════════════════════════════════════════════════════════════
        FEW-SHOT EXAMPLES — 12 patterns covering every query type
        Study every example. Follow these patterns exactly.
        ════════════════════════════════════════════════════════════════

        ── EXAMPLE 1: Simple list query ──
        {"query_type":"list","phase0_classification":"Returns individual LC rows filtered by status and bank.","phase1_decomposition":["Which LCs have status LCIssued?","Which are for BNP bank?"],"phase2_mapping":[{"sub_question":"Status LCIssued","table":"statuses s","columns":["s.application_status"],"nullable":false,"condition":"s.application_status = 'LCIssued'"},{"sub_question":"BNP bank","table":"lc_request_details lrd","columns":["lrd.bank"],"nullable":false,"condition":"UPPER(lrd.bank) LIKE '%BNP%'"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"none"},"sql":"SELECT TOP(200) lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product, c.name AS CustomerName, bu.business_unit_name AS BusinessUnit, ld.lc_number AS LcNumber, COALESCE(ld.amount, lrd.total_amount) AS LcAmount, ISNULL(ld.currency, lrd.currency) AS Currency, ld.lc_issue_date AS LcIssueDate, ld.lc_expire_date AS LcExpiryDate, ld.grace_period AS GracePeriod, ld.payment_terms AS PaymentTerms, s.application_status AS Status, CASE s.application_status WHEN 'LCIssued' THEN 'Issued' ELSE s.application_status END AS StatusLabel, DATEDIFF(DAY, lrd.created_on, GETDATE()) AS DaysPending, lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status = 'LCIssued'\n  AND UPPER(lrd.bank) LIKE '%BNP%'\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY ld.lc_issue_date DESC","responseType":"table","chartType":null,"reasoning":"Filter to LCIssued + BNP bank, TOP(200) default list cap."}

        ── EXAMPLE 2: Aggregate by bank — NO TOP ──
        {"query_type":"aggregate","phase0_classification":"Groups all LCs by bank and sums values — aggregate not list.","phase1_decomposition":["Group LCs by bank","Sum COALESCE amounts per bank","Count status breakdown per bank"],"phase2_mapping":[{"sub_question":"Group by bank","table":"lc_request_details lrd","columns":["lrd.bank"],"nullable":false,"condition":"GROUP BY lrd.bank"},{"sub_question":"Sum amounts","table":"lc_details ld","columns":["ld.amount","lrd.total_amount"],"nullable":true,"condition":"SUM(COALESCE(ld.amount, lrd.total_amount))"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"none"},"sql":"SELECT lrd.bank AS Bank, COUNT(DISTINCT lrd.obj_id) AS LcCount, ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue, ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount, ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount, ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2') THEN 1 ELSE 0 END), 0) AS PendingCount, ISNULL(SUM(CASE WHEN s.application_status='Draft' THEN 1 ELSE 0 END), 0) AS DraftCount, ISNULL(SUM(CASE WHEN s.application_status='Rejected' THEN 1 ELSE 0 END), 0) AS RejectedCount\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nHAVING COUNT(DISTINCT lrd.obj_id) > 0\nORDER BY TotalLcValue DESC","responseType":"bank_chart","chartType":"horizontal_bar","reasoning":"GROUP BY bank, NO TOP on aggregate, ISNULL on all aggregates."}

        ── EXAMPLE 3: Single stat AVG — NO TOP ──
        {"query_type":"single_stat","phase0_classification":"Asks for ONE average number across all issued LCs — single_stat NOT a list.","phase1_decomposition":["Only LCIssued LCs have lc_issue_date","lc_issue_date can be null add IS NOT NULL guard","AVG DATEDIFF between created_on and lc_issue_date grouped by bank"],"phase2_mapping":[{"sub_question":"LCIssued status only","table":"statuses s","columns":["s.application_status"],"nullable":false,"condition":"s.application_status = 'LCIssued'"},{"sub_question":"lc_issue_date is nullable","table":"lc_details ld","columns":["ld.lc_issue_date"],"nullable":true,"condition":"ld.lc_issue_date IS NOT NULL"},{"sub_question":"AVG calculation","table":"lrd + ld","columns":["lrd.created_on","ld.lc_issue_date"],"nullable":false,"condition":"AVG(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date))"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"Switched to single_stat with AVG — a list query with DATEDIFF would not answer this question."},"sql":"SELECT lrd.bank AS Bank, AVG(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date)) AS AvgDaysToIssuance, MIN(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date)) AS MinDays, MAX(DATEDIFF(DAY, lrd.created_on, ld.lc_issue_date)) AS MaxDays, COUNT(*) AS TotalIssuedLCs\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nJOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status = 'LCIssued'\n  AND ld.lc_issue_date IS NOT NULL\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nORDER BY AvgDaysToIssuance DESC","responseType":"metric_cards","chartType":"stacked_bar","reasoning":"AVG DATEDIFF on issued LCs, INNER JOIN lc_details because we know ld exists for LCIssued, grouped by bank, NO TOP."}

        ── EXAMPLE 4: Cross-table EXISTS — avoids row duplication ──
        {"query_type":"list","phase0_classification":"Row-level LC data matching two conditions — list.","phase1_decomposition":["LCs expiring in current calendar month","At least one unpaid invoice exists","Use EXISTS not JOIN to avoid row duplication"],"phase2_mapping":[{"sub_question":"Expiring this month","table":"lc_details ld","columns":["ld.lc_expire_date"],"nullable":true,"condition":"ld.lc_expire_date IS NOT NULL AND ld.lc_expire_date >= DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE()),0) AND ld.lc_expire_date < DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE())+1,0)"},{"sub_question":"Unpaid invoice exists","table":"invoice_details inv","columns":["inv.is_Mark_as_paid","inv.lc_request_id"],"nullable":false,"condition":"EXISTS (SELECT 1 FROM invoice_details i WHERE i.lc_request_id = lrd.obj_id AND i.is_Mark_as_paid = 0)"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"Used EXISTS to avoid row duplication from multiple invoices per LC."},"sql":"SELECT TOP(200) lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product, c.name AS CustomerName, ld.lc_number AS LcNumber, COALESCE(ld.amount, lrd.total_amount) AS LcAmount, ISNULL(ld.currency, lrd.currency) AS Currency, ld.lc_expire_date AS LcExpiryDate, ld.grace_period AS GracePeriod, DATEDIFF(DAY, GETDATE(), ld.lc_expire_date) AS DaysUntilExpiry, s.application_status AS Status, CASE s.application_status WHEN 'LCIssued' THEN 'Issued' ELSE s.application_status END AS StatusLabel, (SELECT COUNT(*) FROM invoice_details i WHERE i.lc_request_id = lrd.obj_id AND i.is_Mark_as_paid = 0) AS UnpaidInvoiceCount, lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND ld.lc_expire_date IS NOT NULL\n  AND ld.lc_expire_date >= DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE()),0)\n  AND ld.lc_expire_date < DATEADD(MONTH,DATEDIFF(MONTH,0,GETDATE())+1,0)\n  AND EXISTS (SELECT 1 FROM invoice_details i WHERE i.lc_request_id = lrd.obj_id AND i.is_Mark_as_paid = 0)\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY ld.lc_expire_date ASC","responseType":"table","chartType":null,"reasoning":"EXISTS subquery avoids row duplication, IS NOT NULL guard on nullable lc_expire_date."}

        ── EXAMPLE 5: Grace period overdue — IS NOT NULL on nullable date ──
        {"query_type":"list","phase0_classification":"Row-level LC data with two combined filters — list.","phase1_decomposition":["Status is LCIssued or PaymentNotDone","grace_period exists and is in the past"],"phase2_mapping":[{"sub_question":"Unpaid status","table":"statuses s","columns":["s.application_status"],"nullable":false,"condition":"s.application_status IN ('LCIssued','PaymentNotDone')"},{"sub_question":"Grace period passed and nullable","table":"lc_details ld","columns":["ld.grace_period"],"nullable":true,"condition":"ld.grace_period IS NOT NULL AND ld.grace_period < GETDATE()"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"Added IS NOT NULL guard on nullable grace_period."},"sql":"SELECT TOP(200) lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product, c.name AS CustomerName, ld.lc_number AS LcNumber, COALESCE(ld.amount, lrd.total_amount) AS LcAmount, ISNULL(ld.currency, lrd.currency) AS Currency, ld.grace_period AS GracePeriod, ld.lc_expire_date AS LcExpiryDate, DATEDIFF(DAY, ld.grace_period, GETDATE()) AS DaysOverdue, s.application_status AS Status, CASE s.application_status WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentNotDone' THEN 'Unpaid' ELSE s.application_status END AS StatusLabel, lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status IN ('LCIssued','PaymentNotDone')\n  AND ld.grace_period IS NOT NULL\n  AND ld.grace_period < GETDATE()\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY ld.grace_period ASC","responseType":"table","chartType":null,"reasoning":"Two-condition filter, unpaid status AND past grace_period, IS NOT NULL guard."}

        ── EXAMPLE 6: Timeline / audit log — TOP(100), ORDER BY ASC ──
        {"query_type":"timeline","phase0_classification":"Audit log events for a specific LC in chronological order — timeline.","phase1_decomposition":["Find lc_request_id for LC number BNP123 checking both lc_number and contract_number","Get all audit_log rows for that request","Join users for actioned_by name"],"phase2_mapping":[{"sub_question":"LC number lookup","table":"lc_details ld / lrd","columns":["ld.lc_number","lrd.contract_number"],"nullable":true,"condition":"UPPER(ISNULL(ld.lc_number,'')) = 'BNP123' OR UPPER(lrd.contract_number) = 'BNP123'"},{"sub_question":"Audit events","table":"lc_audit_log al","columns":["al.action","al.log_type","al.actioned_on","al.comment"],"nullable":false,"condition":"JOIN al ON al.lc_request_id = lrd.obj_id"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"none"},"sql":"SELECT TOP(100) al.obj_id AS LogId, al.action AS Action, al.log_type AS LogType, al.comment AS Comment, al.actioned_on AS ActionedOn, u.first_name + ' ' + u.last_name AS ActionedBy, lrd.obj_id AS RequestId, ISNULL(ld.lc_number, lrd.contract_number) AS LcNumber, c.name AS CustomerName, lrd.bank AS Bank, s.application_status AS Status\nFROM lc_audit_log al\nJOIN lc_request_details lrd ON lrd.obj_id = al.lc_request_id\nJOIN users u ON u.obj_id = al.actioned_by\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND (UPPER(ISNULL(ld.lc_number,'')) = 'BNP123' OR UPPER(lrd.contract_number) = 'BNP123')\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY al.actioned_on ASC","responseType":"timeline","chartType":null,"reasoning":"Audit log with TOP(100), search both lc_number and contract_number, ORDER BY date ASC."}

        ── EXAMPLE 7: Comparison of 2 groups — NO TOP ──
        {"query_type":"comparison","phase0_classification":"Exactly 2 groups side-by-side — comparison, NO TOP.","phase1_decomposition":["Filter to only BNP and KBC","Aggregate total value and counts per bank"],"phase2_mapping":[{"sub_question":"Filter to 2 banks","table":"lc_request_details lrd","columns":["lrd.bank"],"nullable":false,"condition":"UPPER(lrd.bank) IN ('BNP','KBC')"},{"sub_question":"Aggregate per bank","table":"lrd + ld","columns":["ld.amount","lrd.total_amount"],"nullable":true,"condition":"SUM(COALESCE(ld.amount, lrd.total_amount))"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"none"},"sql":"SELECT lrd.bank AS Bank, COUNT(DISTINCT lrd.obj_id) AS LcCount, ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue, ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount, ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount, ISNULL(SUM(CASE WHEN s.application_status IN ('Submitted_N+1','Submitted_N+2') THEN 1 ELSE 0 END), 0) AS PendingCount\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND UPPER(lrd.bank) IN ('BNP','KBC')\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.bank\nORDER BY TotalLcValue DESC","responseType":"comparison","chartType":"side_by_side","reasoning":"WHERE limits to 2 banks, GROUP BY bank, NO TOP on aggregate."}

        ── EXAMPLE 8: Approval list — stuck in approval queue ──
        {"query_type":"list","phase0_classification":"Row-level LC data filtered by status and age — list.","phase1_decomposition":["Status is in approval stages","Created more than 5 days ago"],"phase2_mapping":[{"sub_question":"Approval status filter","table":"statuses s","columns":["s.application_status"],"nullable":false,"condition":"s.application_status IN ('Submitted_For_Validation','Submitted_N+1','Submitted_N+2')"},{"sub_question":"More than 5 days","table":"lc_request_details lrd","columns":["lrd.created_on"],"nullable":false,"condition":"DATEDIFF(DAY, lrd.created_on, GETDATE()) > 5"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"none"},"sql":"SELECT TOP(200) lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product, c.name AS CustomerName, bu.business_unit_name AS BusinessUnit, COALESCE(ld.amount, lrd.total_amount) AS LcAmount, ISNULL(ld.currency, lrd.currency) AS Currency, lrd.created_on AS RequestCreatedOn, DATEDIFF(DAY, lrd.created_on, GETDATE()) AS DaysPending, s.application_status AS Status, CASE s.application_status WHEN 'Submitted_For_Validation' THEN 'In Validation' WHEN 'Submitted_N+1' THEN 'Pending N+1' WHEN 'Submitted_N+2' THEN 'Pending N+2' ELSE s.application_status END AS StatusLabel\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND s.application_status IN ('Submitted_For_Validation','Submitted_N+1','Submitted_N+2')\n  AND DATEDIFF(DAY, lrd.created_on, GETDATE()) > 5\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nORDER BY DaysPending DESC","responseType":"approval_list","chartType":null,"reasoning":"Approval stages plus DATEDIFF greater than 5, TOP(200), sorted most urgent first."}

        ── EXAMPLE 9: Status breakdown — GROUP BY, NO TOP, doughnut chart ──
        {"query_type":"aggregate","phase0_classification":"Groups all LCs by status and counts them — aggregate, NO TOP.","phase1_decomposition":["Group all active LCs by application_status","Count per status","Add HumanLabel CASE for display"],"phase2_mapping":[{"sub_question":"Group by status","table":"statuses s","columns":["s.application_status"],"nullable":false,"condition":"GROUP BY s.application_status"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"none"},"sql":"SELECT s.application_status AS Status, CASE s.application_status WHEN 'Draft' THEN 'Draft' WHEN 'Submitted_For_Validation' THEN 'In Validation' WHEN 'Submitted_N+1' THEN 'Pending N+1' WHEN 'Submitted_N+2' THEN 'Pending N+2' WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentDone' THEN 'Paid' WHEN 'PaymentNotDone' THEN 'Unpaid' WHEN 'Rejected' THEN 'Rejected' WHEN 'Cancelled' THEN 'Cancelled' ELSE s.application_status END AS HumanLabel, COUNT(DISTINCT lrd.obj_id) AS Count, ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalAmount\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY s.application_status\nORDER BY Count DESC","responseType":"metric_cards","chartType":"doughnut","reasoning":"GROUP BY status, NO TOP, HumanLabel CASE for display, ISNULL on aggregates."}

        ── EXAMPLE 10: Amendment query with HAVING — not WHERE ──
        {"query_type":"list","phase0_classification":"Row-level data filtered by amendment count using HAVING — list.","phase1_decomposition":["Join amendment table to lrd","Count amendments per LC","HAVING COUNT greater than 2 not WHERE because it is an aggregate filter"],"phase2_mapping":[{"sub_question":"Join amendments","table":"lc_amendment_details lad","columns":["lad.lc_request_id","lad.obj_id"],"nullable":false,"condition":"LEFT JOIN lad ON lad.lc_request_id = lrd.obj_id"},{"sub_question":"HAVING count filter","table":"lad","columns":["lad.obj_id"],"nullable":false,"condition":"HAVING COUNT(lad.obj_id) > 2"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"Used HAVING not WHERE for aggregate count filter."},"sql":"SELECT TOP(200) lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product, c.name AS CustomerName, ISNULL(ld.lc_number, lrd.contract_number) AS LcNumber, COALESCE(ld.amount, lrd.total_amount) AS LcAmount, ISNULL(ld.currency, lrd.currency) AS Currency, COUNT(lad.obj_id) AS AmendmentCount, MAX(lad.created_on) AS LastAmendedOn, s.application_status AS Status, CASE s.application_status WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentDone' THEN 'Paid' ELSE s.application_status END AS StatusLabel\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nLEFT JOIN lc_amendment_details lad ON lad.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.obj_id, lrd.bank, lrd.product, c.name, ld.lc_number, lrd.contract_number, ld.amount, lrd.total_amount, ld.currency, lrd.currency, s.application_status\nHAVING COUNT(lad.obj_id) > 2\nORDER BY AmendmentCount DESC","responseType":"table","chartType":null,"reasoning":"LEFT JOIN amendments, GROUP BY all non-aggregates, HAVING for count filter, TOP(200)."}

        ── EXAMPLE 11: Top-N user-specified — TOP matches user's number exactly ──
        {"query_type":"aggregate","phase0_classification":"User explicitly said top 5 — aggregate with TOP(5).","phase1_decomposition":["Group by customer","Sum total LC value","Return only top 5 rows"],"phase2_mapping":[{"sub_question":"Group by customer","table":"customers c","columns":["c.name","c.sap_sold_to_name"],"nullable":false,"condition":"GROUP BY c.name, c.sap_sold_to_name"},{"sub_question":"Top 5 from user","table":"n/a","columns":[],"nullable":false,"condition":"TOP(5) with ORDER BY TotalLcValue DESC"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"TOP(5) because user said top 5 not the default TOP(200)."},"sql":"SELECT TOP(5) c.name AS CustomerName, c.sap_sold_to_name AS SapCustomerName, COUNT(DISTINCT lrd.obj_id) AS LcCount, ISNULL(SUM(COALESCE(ld.amount, lrd.total_amount)), 0) AS TotalLcValue, ISNULL(SUM(CASE WHEN s.application_status='LCIssued' THEN 1 ELSE 0 END), 0) AS IssuedCount, ISNULL(SUM(CASE WHEN s.application_status='PaymentDone' THEN 1 ELSE 0 END), 0) AS PaidCount\nFROM customers c\nJOIN lc_request_details lrd ON lrd.customer_id = c.obj_id\nJOIN statuses s ON s.obj_id = lrd.status_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY c.name, c.sap_sold_to_name\nORDER BY TotalLcValue DESC","responseType":"metric_cards","chartType":"stacked_bar","reasoning":"User said top 5 so TOP(5), GROUP BY customer, ORDER BY value descending."}

        ── EXAMPLE 12: Invoice cross-table with GROUP BY to prevent row duplication ──
        {"query_type":"list","phase0_classification":"Row-level LCs with a date condition from invoice_details — list.","phase1_decomposition":["beneficiary_pmt_date is in invoice_details need extra JOIN","Date must be NOT NULL and in the past","GROUP BY to prevent duplicate LC rows from multiple invoices per LC"],"phase2_mapping":[{"sub_question":"Find beneficiary_pmt_date which is nullable","table":"invoice_details inv","columns":["inv.beneficiary_pmt_date"],"nullable":true,"condition":"inv.beneficiary_pmt_date IS NOT NULL AND inv.beneficiary_pmt_date < GETDATE()"},{"sub_question":"Avoid row duplication from multiple invoices","table":"invoice_details inv","columns":["inv.lc_request_id"],"nullable":false,"condition":"GROUP BY all lrd/ld/s/c columns, MIN(beneficiary_pmt_date) for the earliest overdue date"}],"phase4_review":{"answers_the_question":true,"correct_query_structure":true,"null_safety_applied":true,"scope_filter_present":true,"fix_applied":"GROUP BY to get 1 row per LC not 1 row per invoice. IS NOT NULL guard on nullable beneficiary_pmt_date."},"sql":"SELECT TOP(200) lrd.obj_id AS RequestId, lrd.bank AS Bank, lrd.product AS Product, c.name AS CustomerName, bu.business_unit_name AS BusinessUnit, ld.lc_number AS LcNumber, COALESCE(ld.amount, lrd.total_amount) AS LcAmount, ISNULL(ld.currency, lrd.currency) AS Currency, ld.lc_expire_date AS LcExpiryDate, ld.grace_period AS GracePeriod, s.application_status AS Status, CASE s.application_status WHEN 'LCIssued' THEN 'Issued' WHEN 'PaymentNotDone' THEN 'Unpaid' WHEN 'PaymentDone' THEN 'Paid' ELSE s.application_status END AS StatusLabel, MIN(inv.beneficiary_pmt_date) AS BeneficiaryPaymentDate, DATEDIFF(DAY, MIN(inv.beneficiary_pmt_date), GETDATE()) AS DaysOverdue, COUNT(inv.obj_id) AS InvoiceCount, lrd.created_on AS RequestCreatedOn\nFROM lc_request_details lrd\nJOIN statuses s ON s.obj_id = lrd.status_id\nJOIN customers c ON c.obj_id = lrd.customer_id\nJOIN business_unit bu ON bu.obj_id = lrd.business_unit_id\nLEFT JOIN lc_details ld ON ld.lc_request_id = lrd.obj_id\nJOIN invoice_details inv ON inv.lc_request_id = lrd.obj_id\nWHERE lrd.is_active = 1\n  AND inv.beneficiary_pmt_date IS NOT NULL\n  AND inv.beneficiary_pmt_date < GETDATE()\n  AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)\nGROUP BY lrd.obj_id, lrd.bank, lrd.product, c.name, bu.business_unit_name, ld.lc_number, ld.amount, lrd.total_amount, ld.currency, lrd.currency, ld.lc_expire_date, ld.grace_period, s.application_status, lrd.created_on\nORDER BY MIN(inv.beneficiary_pmt_date) ASC","responseType":"table","chartType":null,"reasoning":"JOIN invoice_details, GROUP BY to get 1 row per LC, MIN for earliest overdue date, IS NOT NULL guard."}
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
