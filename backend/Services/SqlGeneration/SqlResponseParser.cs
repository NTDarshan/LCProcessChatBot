using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace backend.Services.SqlGeneration;

/// <summary>
/// Parses the raw JSON string returned by Azure OpenAI, validates the extracted
/// SQL through SqlValidationService, and returns a SqlGenerationResult.
/// Single responsibility: parse + validate only. No AI calls, no prompt building.
/// </summary>
public sealed class SqlResponseParser
{
    private readonly SqlValidationService _validator;
    private readonly ILogger<SqlResponseParser> _logger;

    public SqlResponseParser(SqlValidationService validator, ILogger<SqlResponseParser> logger)
    {
        _validator = validator;
        _logger    = logger;
    }

    // ─── PARSE AND VALIDATE ───────────────────────────────────────────────────
    public SqlGenerationResult ParseAndValidate(string raw)
    {
        // Strip any accidental markdown fencing
        raw = Regex.Replace(raw, @"^```(?:json)?\s*", "", RegexOptions.Multiline).Trim();
        raw = Regex.Replace(raw, @"```\s*$",          "", RegexOptions.Multiline).Trim();

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
            sql:          validation.Sql,
            responseType: parsed.ResponseType ?? "table",
            chartType:    parsed.ChartType,
            queryType:    parsed.QueryType ?? "list",
            reasoning:    parsed.Reasoning ?? string.Empty);
    }

    // ─── INNER TYPES ──────────────────────────────────────────────────────────
    private sealed record SqlGenerationOutput(
        [property: JsonPropertyName("query_type")]            string? QueryType,
        [property: JsonPropertyName("phase0_classification")] string? Phase0Classification,
        [property: JsonPropertyName("phase1_decomposition")]  string[]? Phase1Decomposition,
        [property: JsonPropertyName("phase2_mapping")]        object[]? Phase2Mapping,
        [property: JsonPropertyName("phase4_review")]         Phase4Review? Phase4Review,
        [property: JsonPropertyName("sql")]                   string? Sql,
        [property: JsonPropertyName("responseType")]          string? ResponseType,
        [property: JsonPropertyName("chartType")]             string? ChartType,
        [property: JsonPropertyName("reasoning")]             string? Reasoning);

    private sealed record Phase4Review(
        [property: JsonPropertyName("answers_the_question")]    bool AnswersTheQuestion,
        [property: JsonPropertyName("correct_query_structure")] bool CorrectQueryStructure,
        [property: JsonPropertyName("null_safety_applied")]     bool NullSafetyApplied,
        [property: JsonPropertyName("scope_filter_present")]    bool ScopeFilterPresent,
        [property: JsonPropertyName("fix_applied")]             string FixApplied);
}
