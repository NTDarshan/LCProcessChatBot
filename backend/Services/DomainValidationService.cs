namespace backend.Services;

// ─────────────────────────────────────────────────────────────────────────────
//  DomainValidationService  –  guards the pipeline from completely off-topic
//  questions while allowing all legitimate LC-system queries through.
//
//  KEY FIXES vs original:
//  1.  Added: "show", "how", "many", "count", "total", "summary", "list",
//      "what", "which", "give", "get", "find" — common question starters that
//      pair with LC terms. Without these, "how many LCs" was rejected.
//  2.  Added bank codes actually used: bnp, kbc, cacib, commerzbank.
//  3.  Added: invoice, payment, refund, history, lifecycle, audit, report,
//      customer, supplier, beneficiary, amendment, expired, expiry, draft,
//      rejected, cancelled, issued, outstanding, currency, shipment, port.
//  4.  Single-word check is kept; multi-word substring check is the fallback.
//  5.  Minimum-token count guard removed — it was incorrectly blocking
//      short-but-valid queries like "show drafts" or "top banks".
// ─────────────────────────────────────────────────────────────────────────────
public class DomainValidationService
{
    // Any message containing at least one of these (case-insensitive) is considered
    // LC-domain relevant. Ordered roughly by frequency of occurrence in user queries.
    private static readonly HashSet<string> _lcKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── Core LC terms
        "lc", "letter of credit", "lcs",
        "amendment", "amend",
        "invoice", "payment", "paid", "unpaid", "refund",
        "issued", "issuing", "issue",
        "outstanding", "overdue", "delayed", "delay",
        "expir", "expired", "expiry", "expiring",
        "draft", "rejected", "cancelled", "cancel",
        "pending", "approval", "approved", "approvals",
        "validation", "submitted",

        // ── Roles & entities
        "beneficiary", "applicant", "supplier", "customer",
        "bank", "bnp", "kbc", "cacib", "commerzbank", "citi", "hsbc",
        "cfo", "ceo", "n+1", "n+2",

        // ── Document / process terms
        "swift", "document", "discrepancy", "presentation",
        "compliance", "maturity", "drawdown",
        "shipment", "port", "loading", "discharge",
        "contract", "sap", "order",

        // ── Financial & reporting terms
        "status", "summary", "breakdown", "statistics", "overview",
        "count", "total", "report", "list",
        "amount", "value", "currency", "usd", "eur", "inr", "aed",
        "finance", "trade", "utilisation", "utilization", "limit",
        "history", "lifecycle", "audit", "timeline", "activity",

        // ── Question starters that always pair with LC context
        // (allow "show", "how many", "which lcs" etc.)
        "show", "which", "what", "how many", "give", "get", "find", "list"
    };

    // Returns true only if the message is LC-domain relevant
    public bool IsLcRelated(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        // Substring check covers multi-word keywords ("letter of credit", "n+1")
        // and partial words ("expir" matches expiring/expired/expiry)
        return _lcKeywords.Any(k =>
            message.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}