namespace backend.Services.SqlGeneration;

/// <summary>
/// Provides SQL generation rules including business rules (R1–R9) and
/// the TOP(N) rule set (R7). All SQL constraints the AI must follow.
/// </summary>
public sealed class QueryRuleProvider
{
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

    /// <summary>Returns the SQL rule set including TOP(N) rules (R7).</summary>
    public string GetRules() => TopNRules;
}
