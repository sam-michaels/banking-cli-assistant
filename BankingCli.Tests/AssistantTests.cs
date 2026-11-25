using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using static BankingCli.Tests.FakeChatHandler;

namespace BankingCli.Tests;

public sealed class AssistantTests
{
    private static string Balances(BankSession b) => b.Execute("balance").Output;

    // The JSON the model returns for the strict interpretation schema: every field present, unused ones null.
    private static string Interp(string action, string? account = null, string? from = null, string? to = null, decimal? amount = null, string? question = null) =>
        new JsonObject
        {
            ["action"] = action,
            ["account"] = account,
            ["from"] = from,
            ["to"] = to,
            ["amount"] = amount,
            ["question"] = question
        }.ToJsonString();

    [Fact]
    public async Task StarterSession_IsByteIdentical_AndMakesNoApiCalls()
    {
        string[] lines = ["deposit savings 10", "transfer savings chequing 4", "withdraw chequing 1", "balance", "help", "?", "exit"];
        FakeChatHandler handler = new FakeChatHandler();
        Assistant assistant = new Assistant(new BankSession(), FakeChat(handler));
        BankSession starter = new BankSession();
        CommandResult actual = default;

        foreach (string line in lines)
        {
            CommandResult expected = starter.Execute(line);
            actual = await assistant.HandleAsync(line);
            Assert.Equal(expected, actual);
        }

        Assert.True(actual.ShouldExit);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("balance please", "ERROR INVALID_ACCOUNT")]
    [InlineData("deposit savings five", "ERROR INVALID_AMOUNT")]
    public async Task InvalidAccountOrAmount_KeepsStarterError_NoApiCall(string input, string expected)
    {
        FakeChatHandler handler = new FakeChatHandler();
        Assistant assistant = new Assistant(new BankSession(), FakeChat(handler));

        CommandResult result = await assistant.HandleAsync(input);

        Assert.Equal(expected, result.Output);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyInput_NoApiCall(string input)
    {
        FakeChatHandler handler = new FakeChatHandler();
        Assistant assistant = new Assistant(new BankSession(), FakeChat(handler));

        CommandResult result = await assistant.HandleAsync(input);

        Assert.Equal("", result.Output);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task NoKey_EnglishGetsStarterOutput()
    {
        Assistant assistant = new Assistant(new BankSession(), null);

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing");

        Assert.Equal("ERROR UNKNOWN_COMMAND", result.Output);
    }

    [Fact]
    public async Task Clarify_PrintsQuestion_NoChange()
    {
        BankSession bank = new BankSession();
        string before = Balances(bank);
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("clarify", question: "How much?"));
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("move some money to chequing");

        Assert.Equal("How much?", result.Output);
        Assert.Equal(before, Balances(bank));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TwoInterpretCalls_OnSameAssistant_BothSucceed()
    {
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("clarify", question: "How much?"));
        handler.EnqueueContent(Interp("clarify", question: "Which account?"));
        Assistant assistant = new Assistant(new BankSession(), FakeChat(handler));

        CommandResult first = await assistant.HandleAsync("move some money to chequing");
        CommandResult second = await assistant.HandleAsync("please deposit twenty dollars");

        Assert.Equal("How much?", first.Output);
        Assert.Equal("Which account?", second.Output);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MissingRequiredField_AsksForDetail_NoChange()
    {
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        string before = Balances(bank);
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("transfer", from: null, to: "chequing", amount: 5, question: null));
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move $5 to chequing");

        Assert.Equal(Assistant.NeedMoreDetail, result.Output);
        Assert.Equal(before, Balances(bank));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, null)]
    [InlineData(HttpStatusCode.InternalServerError, null)]
    [InlineData(HttpStatusCode.OK, "not json")]
    public async Task InterpretFailure_PrintsUnavailable_NoChange(HttpStatusCode status, string? content)
    {
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        string before = Balances(bank);
        FakeChatHandler handler = new FakeChatHandler();
        if (content is null)
        {
            handler.Enqueue(status, "{}");
        }
        else
        {
            handler.EnqueueContent(content);
        }

        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing");

        Assert.Equal(Assistant.Unavailable, result.Output);
        Assert.Equal(before, Balances(bank));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void BuildCommand_IgnoresIrrelevantFields()
    {
        Interpretation i = new Interpretation("deposit", "savings", "chequing", "savings", 5m, null);

        Assert.Equal("deposit savings 5", Assistant.BuildCommand(i));
    }

    [Fact]
    public void BuildCommand_FormatsAmountInvariant()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
            Interpretation i = new Interpretation("withdraw", "chequing", null, null, 4.5m, null);

            Assert.Equal("withdraw chequing 4.5", Assistant.BuildCommand(i));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task Interpreted_RunsCommandOnce_PrintsRawForNow()
    {
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("transfer", from: "savings", to: "chequing", amount: 4));
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing");

        Assert.Contains("TRANSFER $4.00", result.Output);
        Assert.Equal("chequing=$4.00 | savings=$6.00", Balances(bank));
        Assert.Equal(1, handler.Calls);
    }
}
