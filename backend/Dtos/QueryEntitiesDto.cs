namespace backend.Dtos;

// Entities extracted from the user's raw message (all nullable – extract only what is present)
public class QueryEntitiesDto
{
    // LC reference number extracted via regex e.g. "LC1023"
    public string? LcNumber { get; set; }

    // Issuing bank name extracted from context e.g. "HDFC"
    public string? BankName { get; set; }

    // Customer / applicant name extracted from context e.g. "ArcelorMittal"
    public string? CustomerName { get; set; }

    // Minimum LC amount threshold e.g. "above 1 crore" → 10_000_000
    public decimal? MinAmount { get; set; }

    // Maximum LC amount threshold e.g. "below 5 crore" → 50_000_000
    public decimal? MaxAmount { get; set; }

    // Number of days for relative date windows e.g. "next 15 days" → 15
    public int? DaysRange { get; set; }

    // Explicit start date for absolute date range queries
    public DateTime? StartDate { get; set; }

    // Explicit end date for absolute date range queries
    public DateTime? EndDate { get; set; }

    // Mapped application_status value extracted from intent keywords e.g. "issued" → "LCIssued"
    // For multi-value statuses (pending) this holds the primary value; SQL handles IN() separately
    public string? Status { get; set; }

    // True when "pending" keyword detected – SQL must use IN('Submitted_N+1','Submitted_N+2')
    public bool IsPendingStatus { get; set; }

    // Country name extracted from phrases like "for Germany" or "in India"
    public string? Country { get; set; }

    // Currency code extracted from explicit mentions like "USD", "EUR", "INR", "GBP"
    public string? CurrencyCode { get; set; }
}
