using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace BankingCli;

// Minimal client for an OpenAI-compatible Chat Completions endpoint. Every failure throws; callers decide the fallback.
internal sealed class OpenAiChat(HttpClient http, Uri baseUrl, string apiKey, string model)
{
    private const string DefaultBaseUrl = "https://api.openai.com/v1";
    private const string DefaultModel = "gpt-4.1-mini";

    private readonly Uri endpoint = new Uri(baseUrl.ToString().TrimEnd('/') + "/chat/completions");

    public static OpenAiChat? FromEnvironment()
    {
        string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        string? baseUrl = Environment.GetEnvironmentVariable("OPENAI_BASE_URL");
        string? model = Environment.GetEnvironmentVariable("OPENAI_MODEL");
        HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        return new OpenAiChat(
            http,
            new Uri(string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl),
            apiKey,
            string.IsNullOrWhiteSpace(model) ? DefaultModel : model);
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage, JsonObject? jsonSchema = null)
    {
        // No temperature, top_p or max_tokens: some current models reject them.
        JsonObject body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userMessage })
        };

        if (jsonSchema is not null)
        {
            body["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "bank_request",
                    ["strict"] = true,
                    // A JsonNode can only have one parent, so a shared schema must be copied per request.
                    ["schema"] = jsonSchema.DeepClone()
                }
            };
        }

        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        string? content = JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("The chat API returned no content.");
        }

        return content;
    }
}
