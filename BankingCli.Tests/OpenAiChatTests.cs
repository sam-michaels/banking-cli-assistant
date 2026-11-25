using System.Net;
using System.Text.Json.Nodes;

namespace BankingCli.Tests;

public sealed class OpenAiChatTests
{
    private static readonly JsonObject Schema = new JsonObject { ["type"] = "object" };

    [Fact]
    public async Task CompleteAsync_PostsToChatCompletions_AndReturnsContent()
    {
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent("hi");

        string result = await FakeChatHandler.FakeChat(handler).CompleteAsync("system text", "user text", Schema);

        Assert.Equal("hi", result);
        (Uri url, string? auth, string body) = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://fake.test/v1/chat/completions"), url);
        Assert.Equal("Bearer test-key", auth);

        JsonNode json = JsonNode.Parse(body)!;
        Assert.Equal("test-model", (string?)json["model"]);
        Assert.Equal("system", (string?)json["messages"]![0]!["role"]);
        Assert.Equal("user", (string?)json["messages"]![1]!["role"]);
        Assert.Equal("json_schema", (string?)json["response_format"]!["type"]);
        Assert.True((bool?)json["response_format"]!["json_schema"]!["strict"]);
        Assert.False(json.AsObject().ContainsKey("temperature"));
    }

    [Fact]
    public async Task CompleteAsync_WithoutSchema_OmitsResponseFormat()
    {
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent("hi");

        await FakeChatHandler.FakeChat(handler).CompleteAsync("system text", "user text");

        JsonNode json = JsonNode.Parse(Assert.Single(handler.Requests).Body)!;
        Assert.False(json.AsObject().ContainsKey("response_format"));
    }

    [Fact]
    public async Task CompleteAsync_Throws_OnHttpError()
    {
        FakeChatHandler handler = new FakeChatHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsAnyAsync<Exception>(() => FakeChatHandler.FakeChat(handler).CompleteAsync("s", "u"));
    }

    [Fact]
    public async Task CompleteAsync_Throws_OnEmptyContent()
    {
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(null);

        await Assert.ThrowsAnyAsync<Exception>(() => FakeChatHandler.FakeChat(handler).CompleteAsync("s", "u"));
    }
}
