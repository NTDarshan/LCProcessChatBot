using System.Text.RegularExpressions;
using backend.Dtos;

namespace backend.Services;

public class EntityExtractionService
{
    private readonly ILogger<EntityExtractionService> _logger;

    // ── LC Number ────────────────────────────────────────────────────────────
    // Matches real LC codes: BNP123, ANTWLI00, VLAALI0014297, CFRITF453,
    //                        06402LCB2201281, KBC2300558 etc.
    // Pattern: 3+ alphanumeric chars that contain at least one letter and one digit
    // preceded by "lc", "number", "for", "#", or end of a quote / space.
    private static readonly Regex _lcNumberRegex = new(
        @"(?:lc\s*(?:number|no|#)?\s*[:\-]?\s*|#\s*)([A-Z0-9]{4,30})\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Fallback: bare standalone token that looks like a bank LC reference code
    // (starts with letter, has digits, 5-20 chars, not a common English word)
    private static readonly Regex _lcCodeBareRegex = new(
        @"\b([A-Z]{2,8}\d{2,}[A-Z0-9]*|[0-9]{4,}[A-Z]{2,}[A-Z0-9]*)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Days Range ───────────────────────────────────────────────────────────
    private static readonly Regex _daysRangeRegex = new(
        @"(?:next|in|last|past|within)\s+(\d+)\s+days?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Amount: minimum ──────────────────────────────────────────────────────
    private static readonly Regex _minAmountRegex = new(
        @"(?:above|over|more\s+than|greater\s+than|exceeding|atleast|at\s+least)\s*([\d,]+(?:\.\d+)?)\s*(billion|million|thousand|mn|k\b)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Amount: maximum ──────────────────────────────────────────────────────
    private static readonly Regex _maxAmountRegex = new(
        @"(?:below|under|less\s+than|upto|up\s+to|within)\s*([\d,]+(?:\.\d+)?)\s*(billion|million|thousand|mn|k\b)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Amount: between range ────────────────────────────────────────────────
    private static readonly Regex _betweenAmountRegex = new(
        @"between\s*([\d,]+(?:\.\d+)?)\s*(?:and|to|-)\s*([\d,]+(?:\.\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Bank name: direct known-code match ───────────────────────────────────
    // These exact codes appear in lrd.bank column.
    // Checked BEFORE the generic "X bank" pattern so "show BNP LCs" is detected.
    private static readonly Regex _knownBankCodeRegex = new(
        @"\b(BNP|KBC|CACIB|COMMERZBANK|CITI|HSBC|BARCLAYS|NATIXIS|RABOBANK|ING|ABN|UNICREDIT)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Bank name: generic "X bank" or "bank X" ─────────────────────────────
    private static readonly Regex _bankPhraseRegex = new(
        @"(?:(\w+)\s+bank|bank\s+(\w+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Words that must never be treated as bank names
    private static readonly HashSet<string> _bankExclusions =
        new(StringComparer.OrdinalIgnoreCase)
        { "issuing", "top", "the", "a", "an", "by", "best", "all", "any", "our", "my" };

    // ── Customer / company name ───────────────────────────────────────────────
    // Pattern 1: "for <Name>" or "of <Name>" where Name starts with capital
    private static readonly Regex _customerForRegex = new(
        @"(?:for|of)\s+([A-Z][A-Za-z0-9\s&\-\.]{2,40})(?:\s+(?:lc|letter|bank|above|below|pending|issued|outstanding|expir)|\.|,|$)",
        RegexOptions.Compiled);

    // Pattern 2: name inside double-quotes  →  "Tata Steel"
    private static readonly Regex _customerQuotedRegex = new(
        @"""([^""]{3,50})""",
        RegexOptions.Compiled);

    // ── Country ───────────────────────────────────────────────────────────────
    // Extended for European countries relevant to ArcelorMittal / LC ports
    private static readonly Regex _countryRegex = new(
        @"\b(?:for|in|from|to)\s+(germany|france|belgium|netherlands|luxembourg|india|usa|uk|china|japan|korea|vietnam|romania|chile|guatemala|egypt|singapore|australia|brazil|uae|turkey)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Currency ─────────────────────────────────────────────────────────────
    private static readonly Regex _currencyRegex = new(
        @"\b(USD|EUR|INR|GBP|AED|CNY|AZN|CAD|DKK)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Status keyword rules ─────────────────────────────────────────────────
    // Ordered: most-specific first; first match wins.
    // Status values aligned with what statuses table actually contains.
    private static readonly (string[] Keywords, string Status, bool IsPending)[] _statusRules =
    [
        (["n+1"],                                        "Submitted_N+1",            false),
        (["n+2"],                                        "Submitted_N+2",            false),
        (["submitted_n+1"],                              "Submitted_N+1",            false),
        (["submitted_n+2"],                              "Submitted_N+2",            false),
        (["submitted", "validation"],                    "Submitted_For_Validation", false),
        (["submitted_for_validation"],                   "Submitted_For_Validation", false),
        (["pending", "approval"],                        "Submitted_N+1",            true),
        (["pending", "approvals"],                       "Submitted_N+1",            true),
        (["pending"],                                    "Submitted_N+1",            true),
        (["payment", "not", "done"],                     "PaymentNotDone",           false),
        (["payment", "done"],                            "PaymentDone",              false),
        (["paymentnotdone"],                             "PaymentNotDone",           false),
        (["paymentdone"],                                "PaymentDone",              false),
        (["unpaid"],                                     "PaymentNotDone",           false),
        (["outstanding"],                                "LCIssued",                 false),
        (["issued"],                                     "LCIssued",                 false),
        (["lcissued"],                                   "LCIssued",                 false),
        (["paid"],                                       "PaymentDone",              false),
        (["rejected"],                                   "Rejected",                 false),
        (["cancelled"],                                  "Cancelled",                false),
        (["cancel"],                                     "Cancelled",                false),
        (["draft"],                                      "Draft",                    false),
        (["validation"],                                 "Submitted_For_Validation", false),
    ];

    public EntityExtractionService(ILogger<EntityExtractionService> logger)
    {
        _logger = logger;
    }

    // ── Entry point ───────────────────────────────────────────────────────────
    public QueryEntitiesDto Extract(string message)
    {
        var dto = new QueryEntitiesDto();
        var input = message.Trim();
        var lower = input.ToLowerInvariant();

        ExtractLcNumber(input, dto);
        ExtractMinAmount(input, dto);
        ExtractMaxAmount(input, dto);
        ExtractBetweenAmount(input, dto);
        ExtractDaysRange(lower, dto);
        ExtractBankName(input, dto);
        ExtractCustomerName(input, dto);
        ExtractStatus(lower, dto);
        ExtractCountry(input, dto);
        ExtractCurrencyCode(input, dto);

        _logger.LogInformation(
            "Entities | LC:{Lc} Min:{Min} Max:{Max} Days:{Days} Bank:{Bank} " +
            "Customer:{Cust} Status:{St} Pending:{Pend} Country:{Ctry} CCY:{Ccy}",
            dto.LcNumber, dto.MinAmount, dto.MaxAmount,
            dto.DaysRange, dto.BankName, dto.CustomerName,
            dto.Status, dto.IsPendingStatus, dto.Country, dto.CurrencyCode);

        return dto;
    }

    // ── LC Number ─────────────────────────────────────────────────────────────
    private static void ExtractLcNumber(string message, QueryEntitiesDto dto)
    {
        // Try "lc number: XXXX" style first
        var m = _lcNumberRegex.Match(message);
        if (m.Success)
        {
            dto.LcNumber = m.Groups[1].Value.ToUpperInvariant();
            return;
        }

        // Fallback: bare LC-looking code (e.g. "show BNP123 details")
        m = _lcCodeBareRegex.Match(message);
        if (m.Success)
            dto.LcNumber = m.Groups[1].Value.ToUpperInvariant();
    }

    // ── Minimum Amount ────────────────────────────────────────────────────────
    private static void ExtractMinAmount(string message, QueryEntitiesDto dto)
    {
        var m = _minAmountRegex.Match(message);
        if (!m.Success) return;
        var raw = m.Groups[1].Value.Replace(",", "");
        if (decimal.TryParse(raw, out var n))
            dto.MinAmount = ScaleAmount(n, m.Groups[2].Value);
    }

    // ── Maximum Amount ────────────────────────────────────────────────────────
    private static void ExtractMaxAmount(string message, QueryEntitiesDto dto)
    {
        var m = _maxAmountRegex.Match(message);
        if (!m.Success) return;
        var raw = m.Groups[1].Value.Replace(",", "");
        if (decimal.TryParse(raw, out var n))
            dto.MaxAmount = ScaleAmount(n, m.Groups[2].Value);
    }

    // ── Between Range ─────────────────────────────────────────────────────────
    private static void ExtractBetweenAmount(string message, QueryEntitiesDto dto)
    {
        if (dto.MinAmount.HasValue && dto.MaxAmount.HasValue) return; // already set
        var m = _betweenAmountRegex.Match(message);
        if (!m.Success) return;
        var r1 = m.Groups[1].Value.Replace(",", "");
        var r2 = m.Groups[2].Value.Replace(",", "");
        if (decimal.TryParse(r1, out var lo) && decimal.TryParse(r2, out var hi))
        {
            dto.MinAmount ??= lo;
            dto.MaxAmount ??= hi;
        }
    }

    // ── Days Range ────────────────────────────────────────────────────────────
    private static void ExtractDaysRange(string lower, QueryEntitiesDto dto)
    {
        var m = _daysRangeRegex.Match(lower);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var d))
            dto.DaysRange = d;
    }

    // ── Bank Name ─────────────────────────────────────────────────────────────
    private static void ExtractBankName(string message, QueryEntitiesDto dto)
    {
        // 1. Known bank code first (BNP, KBC, CACIB …)
        var m = _knownBankCodeRegex.Match(message);
        if (m.Success)
        {
            dto.BankName = m.Value.ToUpperInvariant();
            return;
        }

        // 2. Generic "X bank" / "bank X" pattern
        m = _bankPhraseRegex.Match(message);
        if (!m.Success) return;
        var candidate = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        if (!_bankExclusions.Contains(candidate))
            dto.BankName = candidate.ToUpperInvariant();
    }

    // ── Customer Name ─────────────────────────────────────────────────────────
    private static void ExtractCustomerName(string message, QueryEntitiesDto dto)
    {
        // 1. Quoted name: "Tata Steel" or 'JSW Steel'
        var m = _customerQuotedRegex.Match(message);
        if (m.Success)
        {
            dto.CustomerName = m.Groups[1].Value.Trim();
            return;
        }

        // 2. "for/of <Company>" pattern
        m = _customerForRegex.Match(message);
        if (m.Success)
        {
            var candidate = m.Groups[1].Value.Trim();
            // Reject if it's just a single common verb/prep that bled through
            if (candidate.Length > 2 && !IsCommonWord(candidate))
                dto.CustomerName = candidate;
        }
    }

    // ── Status ────────────────────────────────────────────────────────────────
    private static void ExtractStatus(string lower, QueryEntitiesDto dto)
    {
        foreach (var (kws, status, isPending) in _statusRules)
        {
            if (kws.All(k => lower.Contains(k, StringComparison.Ordinal)))
            {
                dto.Status = status;
                dto.IsPendingStatus = isPending;
                return;
            }
        }
    }

    // ── Country ───────────────────────────────────────────────────────────────
    private static void ExtractCountry(string message, QueryEntitiesDto dto)
    {
        var m = _countryRegex.Match(message);
        if (!m.Success) return;
        var raw = m.Groups[1].Value.Trim();
        dto.Country = char.ToUpperInvariant(raw[0]) + raw[1..].ToLowerInvariant();
    }

    // ── Currency Code ─────────────────────────────────────────────────────────
    private static void ExtractCurrencyCode(string message, QueryEntitiesDto dto)
    {
        var m = _currencyRegex.Match(message);
        if (m.Success)
            dto.CurrencyCode = m.Value.ToUpperInvariant();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Scale a numeric value by the given unit suffix
    private static decimal ScaleAmount(decimal n, string unit) =>
        unit.ToLowerInvariant() switch
        {
            "billion" => n * 1_000_000_000m,
            "million" or "mn" => n * 1_000_000m,
            "thousand" or "k" => n * 1_000m,
            _ => n           // treat as raw value
        };

    private static readonly HashSet<string> _commonWords =
        new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "all", "any", "my", "our", "their", "its", "this", "that",
          "these", "those", "some", "many", "few", "more", "most", "much", "no", "not" };

    private static bool IsCommonWord(string s) => _commonWords.Contains(s.Trim());
}