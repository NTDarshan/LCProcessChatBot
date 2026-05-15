namespace backend.Models;

/// <summary>
/// Represents the deterministic routing decision made by ChatService.
/// Drives whether the pipeline uses the predefined IntentRouterService
/// or falls back to the dynamic AI-powered SqlGenerationService.
/// </summary>
public class QueryRoutingDecision
{
    /// <summary>
    /// When <c>true</c> the request is served through IntentRouterService
    /// (predefined, parameterised SQL). When <c>false</c> it falls through
    /// to SqlGenerationService (AI-generated SQL).
    /// </summary>
    public bool UseIntentRouter { get; set; }

    /// <summary>
    /// The intent string as classified by AiUnderstandingService.
    /// May be <c>null</c> when no intent could be determined.
    /// </summary>
    public string? Intent { get; set; }

    /// <summary>
    /// Human-readable explanation of why this routing decision was made.
    /// Useful for diagnostics and logging.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}
