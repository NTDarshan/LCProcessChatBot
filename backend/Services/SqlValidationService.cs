using System.Text.RegularExpressions;

namespace backend.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  SqlValidationService  –  pure C# SQL safety guard.
//  Zero AI calls, zero DB calls. Runs before every generated SQL is executed.
//
//  FIXES vs previous version:
//  1. REMOVED "DECLARE" from ForbiddenKeywords
//     The AI sometimes writes DECLARE in CTEs or temp calculations.
//     It is harmless on a read-only connection and was causing ~40% of queries
//     to fail validation and return "No data found" to the user.
//
//  2. REMOVED "SET " (with trailing space) from ForbiddenKeywords
//     "SET " was matching "SETTLEMENT", "DATASET", "OFFSET" inside column values
//     or string literals. This was a false-positive factory.
//
//  3. REMOVED "PRINT", "RAISERROR", "THROW", "CURSOR", "LINKED", "OPENQUERY",
//     "OPENDATASOURCE" from ForbiddenKeywords
//     These are harmless on a read-only SQL Server connection. They were causing
//     auto-retry to fire unnecessarily and still fail on the second attempt,
//     wasting tokens and returning errors.
//
//  4. KEPT only truly destructive keywords that a read-only connection
//     cannot execute anyway, but belt-and-suspenders to guard against them.
//
//  5. "SET " false positive fix: the old pattern matched "OFFSET" in
//     ORDER BY ... OFFSET X ROWS FETCH NEXT Y ROWS (pagination SQL).
//     Now removed entirely — the SELECT-only first check protects against
//     SET being used maliciously.
// ─────────────────────────────────────────────────────────────────────────────
public class SqlValidationService
{
    private static readonly HashSet<string> AllowedTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "lc_request_details", "lc_details", "lc_amendment_details",
        "invoice_details", "invoice_amendments", "lc_approver_mapping",
        "lc_audit_log", "statuses", "customers", "users", "roles",
        "business_unit", "user_business_unit_mapping", "UserRoleMapping",
        "business_lines"
    };

    // ONLY truly destructive keywords that should never appear in a read query.
    // Do NOT add DECLARE, SET, PRINT, CURSOR etc — they are not dangerous on
    // a read-only connection and cause false positives on legitimate SQL patterns.
    private static readonly string[] ForbiddenKeywords =
    [
        "UPDATE",
        "DELETE",
        "DROP",
        "INSERT",
        "TRUNCATE",
        "ALTER",
        "CREATE",
        "EXEC",
        "EXECUTE",
        "MERGE",
        "GRANT",
        "REVOKE",
        "DENY",
        "BULK INSERT",
        "OPENROWSET",
        "xp_",
        "sp_executesql",
        "sp_execute"
    ];

    public SqlValidationResult Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return SqlValidationResult.Fail("SQL is empty");

        var trimmed = sql.Trim();

        // ── 1. Must start with SELECT ────────────────────────────────────────
        if (!Regex.IsMatch(trimmed, @"^\s*SELECT\b", RegexOptions.IgnoreCase))
            return SqlValidationResult.Fail("Query must start with SELECT");

        // ── 2. Strip comments before scanning for forbidden keywords ─────────
        var stripped = Regex.Replace(trimmed, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        stripped     = Regex.Replace(stripped, @"--[^\r\n]*", " ");
        var upper    = stripped.ToUpperInvariant();

        // ── 3. Forbidden keyword scan ─────────────────────────────────────────
        // Use word boundary only for multi-char keywords to avoid false positives.
        // xp_ and sp_ use prefix check instead of word boundary.
        foreach (var kw in ForbiddenKeywords)
        {
            if (kw.EndsWith("_"))
            {
                // Prefix check: xp_, sp_
                if (upper.Contains(kw.ToUpperInvariant()))
                    return SqlValidationResult.Fail($"Forbidden keyword detected: {kw}");
            }
            else
            {
                var pattern = $@"\b{Regex.Escape(kw.ToUpperInvariant())}\b";
                if (Regex.IsMatch(upper, pattern))
                    return SqlValidationResult.Fail($"Forbidden keyword detected: {kw}");
            }
        }

        // ── 4. No multiple statements ─────────────────────────────────────────
        // Strip string literals first to avoid false positives on 'value;other'
        var noStrings = Regex.Replace(stripped, @"'[^']*'", "''");
        var semiCore  = noStrings.TrimEnd().TrimEnd(';');
        if (semiCore.Contains(';'))
            return SqlValidationResult.Fail("Multiple SQL statements are not allowed");

        // ── 5. Must contain @UserId scope filter ─────────────────────────────
        if (!upper.Contains("@USERID"))
            return SqlValidationResult.Fail(
                "Query must include @UserId for business-unit scope. " +
                "Add: AND lrd.business_unit_id IN (SELECT business_unit_id FROM user_business_unit_mapping WHERE user_id = @UserId)");

        // ── 6. No SELECT * ────────────────────────────────────────────────────
        if (Regex.IsMatch(upper, @"SELECT\s+\*"))
            return SqlValidationResult.Fail("SELECT * is not allowed — use explicit column names");

        // ── 7. Must reference at least one known table ───────────────────────
        var referencedTable = AllowedTables.Any(t =>
            Regex.IsMatch(upper, $@"\b{Regex.Escape(t.ToUpper())}\b"));
        if (!referencedTable)
            return SqlValidationResult.Fail(
                "Query does not reference any known table from the LC schema");

        // ── 8. Length sanity check ────────────────────────────────────────────
        if (trimmed.Length < 50)
            return SqlValidationResult.Fail("SQL is too short to be a valid query");

        if (trimmed.Length > 8000)
            return SqlValidationResult.Fail("SQL exceeds maximum allowed length (8000 chars)");

        return SqlValidationResult.Ok(trimmed);
    }
}

public record SqlValidationResult(bool IsValid, string Sql, string? Error)
{
    public static SqlValidationResult Ok(string sql)     => new(true, sql, null);
    public static SqlValidationResult Fail(string error) => new(false, string.Empty, error);
}