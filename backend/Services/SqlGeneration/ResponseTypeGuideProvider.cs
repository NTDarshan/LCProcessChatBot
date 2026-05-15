namespace backend.Services.SqlGeneration;

/// <summary>
/// Provides the response type selection guide used in the OpenAI system prompt.
/// Describes every visual responseType the AI may choose and the matching chartType values.
/// </summary>
public sealed class ResponseTypeGuideProvider
{
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

    /// <summary>Returns the response type guide prompt fragment.</summary>
    public string GetGuide() => ResponseTypeGuide;
}
