using BankingCli;

OpenAiChat? chat;
try
{
    chat = OpenAiChat.FromEnvironment();
}
catch (Exception e) when (e is ArgumentException or UriFormatException)
{
    // A misconfigured endpoint is refused up front rather than sending the key somewhere unsafe.
    Console.Error.WriteLine($"Configuration error: {e.Message}");
    return 1;
}

Assistant assistant = new Assistant(new BankSession(), chat);

// The notice goes to stderr so redirected stdout stays identical to the starter's.
if (chat is null && !Console.IsInputRedirected)
{
    Console.Error.WriteLine("Plain-English requests are off: set OPENAI_API_KEY to turn them on. Commands work as usual.");
}

while (true)
{
    // Keep redirected output limited to command results so it can be compared line by line.
    if (!Console.IsInputRedirected)
    {
        Console.Write("bank> ");
    }

    string? input = Console.ReadLine();
    if (input is null)
    {
        break;
    }

    CommandResult result = await assistant.HandleAsync(input);
    if (result.Output.Length > 0)
    {
        Console.WriteLine(result.Output);
    }

    if (result.ShouldExit)
    {
        break;
    }
}

return 0;
