using Azure;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend.Services;

public class SuggestedQuestionsService
{
    private readonly ChatClient _chatClient;
    private readonly ILogger<SuggestedQuestionsService> _logger;

    public SuggestedQuestionsService(IConfiguration config, ILogger<SuggestedQuestionsService> logger)
    {
        _logger = logger;
        var endpoint   = config["OpenAI:Endpoint"]!;
        var key        = config["OpenAI:Key"]!;
        var deployment = config["OpenAI:Deployment"]!;
        var client     = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
        _chatClient    = client.GetChatClient(deployment);
    }

    public async Task<string[]> GenerateAsync(
        string userQuestion,
        string responseType,
        IEnumerable<object> dataRows,
        CancellationToken ct)
    {
        try
        {
            var preview = dataRows.Take(3).ToList();
            var previewJson = JsonSerializer.Serialize(preview, new JsonSerializerOptions { WriteIndented = false });

            var systemPrompt =
                "You are an LC trade-finance assistant. Generate exactly 3 short follow-up questions " +
                "the user would naturally ask next. Rules: each question max 60 characters, must be " +
                "specific to the actual data (mention real values like bank names or statuses), must be " +
                "answerable by this chatbot, no duplicates. " +
                "Output ONLY a JSON array of 3 strings. No markdown, no explanation.";

            var userPrompt = $"""
                Original question: {userQuestion}
                Response type: {responseType}
                Data sample (first 3 rows): {previewJson}
                """;

            var messages = new List<ChatMessage>
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            };

            var options = new ChatCompletionOptions
            {
                MaxOutputTokenCount = 250,
                Temperature = 0.3f
            };

            var response = await _chatClient.CompleteChatAsync(messages, options, ct);
            var raw = response.Value.Content[0].Text.Trim();

            // Strip markdown code fences if present
            raw = Regex.Replace(raw, @"^```(?:json)?\s*", "", RegexOptions.Multiline).Trim();
            raw = Regex.Replace(raw, @"\s*```$", "", RegexOptions.Multiline).Trim();

            var result = JsonSerializer.Deserialize<string[]>(raw);
            return result is { Length: > 0 } ? result : [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SuggestedQuestionsService failed — returning empty array");
            return [];
        }
    }
}
