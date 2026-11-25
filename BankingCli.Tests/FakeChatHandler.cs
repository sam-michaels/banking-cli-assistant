using System.Net;
using System.Text.Json.Nodes;

namespace BankingCli.Tests;

// Stands in for the chat API: replays queued responses and records every request.
internal sealed class FakeChatHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> responses = new Queue<Func<HttpResponseMessage>>();

    public int Calls { get; private set; }

    public List<(Uri Url, string? Auth, string Body)> Requests { get; } = new List<(Uri Url, string? Auth, string Body)>();

    public static OpenAiChat FakeChat(FakeChatHandler h) =>
        new OpenAiChat(new HttpClient(h), new Uri("https://fake.test/v1"), "test-key", "test-model");

    public void Enqueue(HttpStatusCode status, string body) =>
        responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body) });

    public void EnqueueContent(string? content)
    {
        JsonObject message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        JsonObject body = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["message"] = message }) };
        Enqueue(HttpStatusCode.OK, body.ToJsonString());
    }

    public void EnqueueThrow(Exception exception) => responses.Enqueue(() => throw exception);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        if (responses.Count == 0)
        {
            Assert.Fail("Unexpected API call");
        }

        return responses.Dequeue()();
    }
}
