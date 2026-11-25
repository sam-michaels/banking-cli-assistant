using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BankingCli;

// Commands go straight to the simulator. Only input it can't parse as a command is sent to the model,
// which picks one operation from a fixed schema; the app builds the command and the simulator validates it.
internal sealed class Assistant(BankSession bank, OpenAiChat? chat)
{
    internal const string Unavailable = "Sorry, I couldn't reach the assistant just now, so nothing was changed. You can still use commands - type help to see them.";
    internal const string NeedMoreDetail = "I need a bit more detail before I change anything. Please include the amount and the account(s) when you try again.";

    private const string InterpretPrompt = """
        You turn a bank customer's message into one banking operation. The customer has two accounts: chequing and savings ("checking" means chequing).
        The only operations are:
        - deposit: needs account and amount.
        - withdraw: needs account and amount.
        - transfer: needs from, to and amount. Both accounts must be named by the customer; never assume the other account.
        - balance: account is optional; leave it null to show both accounts.
        Set fields the operation doesn't use to null.
        Never invent or guess an amount or an account. Amounts written as words are fine ("four dollars" is 4).
        Vague amounts such as "some", "a few", "a little", "everything", "all of it" or "half" are not amounts.
        If any required detail is missing or unclear, use action "clarify" and put a short, friendly question in "question" that asks the customer to include the missing detail when they try again. Leave the other fields null.
        If the message is not a request for one of these operations, use action "clarify" with a question that says you can deposit, withdraw, transfer between chequing and savings, or show balances, and that typing exit leaves.
        """;

    private static readonly JsonObject InterpretationSchema = (JsonObject)JsonNode.Parse("""
        {"type":"object","additionalProperties":false,
         "required":["action","account","from","to","amount","question"],
         "properties":{
          "action":{"type":"string","enum":["deposit","withdraw","transfer","balance","clarify"]},
          "account":{"type":["string","null"],"enum":["chequing","savings",null]},
          "from":{"type":["string","null"],"enum":["chequing","savings",null]},
          "to":{"type":["string","null"],"enum":["chequing","savings",null]},
          "amount":{"type":["number","null"]},
          "question":{"type":["string","null"]}}}
        """)!;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public async Task<CommandResult> HandleAsync(string input)
    {
        CommandResult result = bank.Execute(input);
        if (chat is null || result.Output is not ("ERROR UNKNOWN_COMMAND" or "ERROR INVALID_ARGUMENTS"))
        {
            return result;
        }

        Interpretation? interpretation;
        try
        {
            string json = await chat.CompleteAsync(InterpretPrompt, input, InterpretationSchema);
            interpretation = JsonSerializer.Deserialize<Interpretation>(json, JsonOptions);
        }
        catch (Exception)
        {
            // Any API or parsing failure: nothing has run yet, so just say so.
            return new CommandResult(Unavailable);
        }

        if (interpretation is null)
        {
            return new CommandResult(Unavailable);
        }

        string? command = BuildCommand(interpretation);
        if (command is null)
        {
            bool hasQuestion = interpretation.Action == "clarify" && !string.IsNullOrWhiteSpace(interpretation.Question);
            return new CommandResult(hasQuestion ? interpretation.Question! : NeedMoreDetail);
        }

        // Executed exactly once; the simulator validates accounts and amounts.
        return bank.Execute(command);
    }

    // Uses only the fields the action needs; returns null when one of them is missing.
    internal static string? BuildCommand(Interpretation i)
    {
        string? amount = i.Amount?.ToString(CultureInfo.InvariantCulture);
        return i.Action switch
        {
            "deposit" or "withdraw" when i.Account is not null && amount is not null => $"{i.Action} {i.Account} {amount}",
            "transfer" when i.From is not null && i.To is not null && amount is not null => $"transfer {i.From} {i.To} {amount}",
            "balance" => i.Account is null ? "balance" : $"balance {i.Account}",
            _ => null
        };
    }
}

internal sealed record Interpretation(string Action, string? Account, string? From, string? To, decimal? Amount, string? Question);
