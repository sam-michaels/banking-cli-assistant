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

    // Found in code review: routing on both errors was claimed but only UNKNOWN_COMMAND was exercised.
    [Theory]
    [InlineData("Move four dollars from savings to chequing", "ERROR UNKNOWN_COMMAND")]
    [InlineData("transfer four dollars from savings to chequing", "ERROR INVALID_ARGUMENTS")]
    [InlineData("withdraw chequing", "ERROR INVALID_ARGUMENTS")]
    public async Task UnknownCommandOrWrongArity_GoesToModel(string input, string starterOutput)
    {
        Assert.Equal(starterOutput, new BankSession().Execute(input).Output);
        FakeChatHandler handler = new FakeChatHandler();
        Assistant assistant = new Assistant(new BankSession(), FakeChat(handler));
        handler.EnqueueContent("""{"action":"clarify","account":null,"from":null,"to":null,"amount":null,"question":"How much?"}""");

        CommandResult result = await assistant.HandleAsync(input);

        Assert.Equal("How much?", result.Output);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OverlongInput_IsRefused_NoApiCall()
    {
        FakeChatHandler handler = new FakeChatHandler();
        Assistant assistant = new Assistant(new BankSession(), FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing " + new string('x', 500));

        Assert.Equal(Assistant.TooLong, result.Output);
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
    public async Task Transfer_PrintsReply()
    {
        const string reply = "I moved $4.00 from savings to chequing. You now have $6.00 in savings and $4.00 in chequing.";
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("transfer", from: "savings", to: "chequing", amount: 4));
        handler.EnqueueContent(reply);
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing");

        Assert.Equal(reply, result.Output);
        Assert.Equal(2, handler.Calls);
        string userMessage = JsonNode.Parse(handler.Requests[1].Body)!["messages"]![1]!["content"]!.GetValue<string>();
        Assert.Contains("Customer: Move four dollars from savings to chequing", userMessage);
        Assert.Contains("Command: transfer savings chequing 4", userMessage);
        Assert.Contains("Result: TRANSFER $4.00 FROM savings TO chequing | savings=$6.00 | chequing=$4.00", userMessage);
    }

    [Fact]
    public async Task ReplyFailure_PrintsRawOutput_RunsOnce()
    {
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("transfer", from: "savings", to: "chequing", amount: 4));
        handler.Enqueue(HttpStatusCode.InternalServerError, "{}");
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing");

        Assert.Equal("TRANSFER $4.00 FROM savings TO chequing | savings=$6.00 | chequing=$4.00", result.Output);
        Assert.Equal("chequing=$4.00 | savings=$6.00", Balances(bank));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task ReplyWithWrongAmount_PrintsRawOutput()
    {
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("transfer", from: "savings", to: "chequing", amount: 4));
        handler.EnqueueContent("I moved $4.00 from savings to chequing. You now have $60.00 in savings and $4.00 in chequing.");
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("Move four dollars from savings to chequing");

        Assert.Equal("TRANSFER $4.00 FROM savings TO chequing | savings=$6.00 | chequing=$4.00", result.Output);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Decline_ReplyMayQuoteRequestedAmount()
    {
        const string reply = "I couldn't withdraw $50.00 because chequing only has $0.00.";
        BankSession bank = new BankSession();
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("withdraw", account: "chequing", amount: 50));
        handler.EnqueueContent(reply);
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("take fifty dollars out of chequing");

        Assert.Equal(reply, result.Output);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task RejectedAmount_RunsOnceAndExplains()
    {
        const string reply = "The bank couldn't accept that amount, so nothing was changed. Amounts can have at most two decimal places.";
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        string before = Balances(bank);
        FakeChatHandler handler = new FakeChatHandler();
        handler.EnqueueContent(Interp("withdraw", account: "savings", amount: 4.999m));
        handler.EnqueueContent(reply);
        Assistant assistant = new Assistant(bank, FakeChat(handler));

        CommandResult result = await assistant.HandleAsync("withdraw 4.999 from savings");

        Assert.Equal(reply, result.Output);
        Assert.Equal(before, Balances(bank));
        Assert.Equal(2, handler.Calls);
        Assert.Contains("Result: ERROR INVALID_AMOUNT", handler.Requests[1].Body);
    }

    [Fact]
    public void AmountCheck_AcceptsThousandsSeparators()
    {
        Assert.True(Assistant.PreservesAmounts("I deposited $1,000.00, so savings now has $1,000.00.", "DEPOSIT $1000.00 TO savings | savings=$1000.00", "deposit savings 1000"));
    }

    [Fact]
    public void AmountCheck_RejectsMissingBalance()
    {
        Assert.False(Assistant.PreservesAmounts(
            "I moved $4.00 from savings to chequing, which now has $4.00.",
            "TRANSFER $4.00 FROM savings TO chequing | savings=$6.00 | chequing=$4.00",
            "transfer savings chequing 4"));
    }

    // Found in code review: $4.00 appears twice in the result (amount moved, new chequing balance),
    // so a reply that states it once has dropped a number.
    [Fact]
    public void AmountCheck_RequiresRepeatedAmountsEachTime()
    {
        const string output = "TRANSFER $4.00 FROM savings TO chequing | savings=$0.00 | chequing=$4.00";
        Assert.False(Assistant.PreservesAmounts(
            "I moved $4.00 from savings to chequing, and savings is now $0.00.", output, "transfer savings chequing 4"));
        Assert.True(Assistant.PreservesAmounts(
            "I moved $4.00 from savings to chequing. Savings is now $0.00 and chequing has $4.00.", output, "transfer savings chequing 4"));
    }

    [Theory]
    [InlineData("Move four dollars from savings to chequing", "4")]
    [InlineData("move 20 bucks from chequing to savings", "20")]
    [InlineData("withdraw $4.50 from chequing", "4.5")]
    [InlineData("deposit 1,000 into savings", "1000")]
    [InlineData("move twenty-five dollars from savings to chequing", "25")]
    [InlineData("move 2 hundred from savings to chequing", "200")]
    [InlineData("move 2 hundred from savings to chequing", "2")]
    [InlineData("move 2.5 thousand from savings to chequing", "2500")]
    [InlineData("move 5k from savings to chequing", "5000")]
    [InlineData("move 2 grand from savings to chequing", "2000")]
    [InlineData("move a dozen dollars from savings to chequing", "12")]
    [InlineData("move a grand from savings to chequing", "1000")]
    public void AmountIsGrounded_WhenTheCustomerStatedIt(string input, string amount)
    {
        Assert.True(Assistant.AmountIsGrounded(input, decimal.Parse(amount, CultureInfo.InvariantCulture)));
    }

    [Theory]
    [InlineData("move a few dollars from savings to chequing", "3")]
    [InlineData("move 20 bucks from chequing to savings", "25")]
    [InlineData("put some money in savings", "10")]
    [InlineData("take a buck out of chequing", "1")]
    [InlineData("move 2 hundred from savings to chequing", "300")]
    [InlineData("move 5k from savings to chequing", "500")]
    public void AmountIsNotGrounded_WhenTheModelSuppliedIt(string input, string amount)
    {
        Assert.False(Assistant.AmountIsGrounded(input, decimal.Parse(amount, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task UngroundedAmount_AsksForNumber_NoChange()
    {
        FakeChatHandler handler = new FakeChatHandler();
        BankSession bank = new BankSession();
        bank.Execute("deposit savings 10");
        Assistant assistant = new Assistant(bank, FakeChat(handler));
        handler.EnqueueContent("""{"action":"transfer","account":null,"from":"savings","to":"chequing","amount":3,"question":null}""");

        CommandResult result = await assistant.HandleAsync("move a few dollars from savings to chequing");

        Assert.Equal(Assistant.NeedNumericAmount, result.Output);
        Assert.Equal("chequing=$0.00 | savings=$10.00", bank.Execute("balance").Output);
        Assert.Equal(1, handler.Calls);
    }
}
