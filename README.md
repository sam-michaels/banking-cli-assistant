# banking-cli-assistant

A small C# command-line banking simulator with in-memory chequing and savings accounts, extended so you can type everyday English ("Move four dollars from savings to chequing") and get a conversational reply. It is built on [banking-cli-starter](https://github.com/dpo007/banking-cli-starter): the starter's commands still work exactly as before, and the simulator (`BankSession.cs`) is unchanged. English requests go to an OpenAI-compatible API. The design, including the options considered for each decision, is in [`docs/DESIGN.md`](docs/DESIGN.md).

## Build, run, test

With the .NET 10 SDK installed, run these from the repo root.

```sh
dotnet run --project BankingCli
dotnet test
```

Both accounts start at $0.00 each time you run the program. The tests need no API key.

## Configuration

| Variable | Default | Notes |
| --- | --- | --- |
| `OPENAI_API_KEY` | none | Unset → commands only, exactly like the starter. |
| `OPENAI_BASE_URL` | `https://api.openai.com/v1` | Any OpenAI-compatible endpoint. |
| `OPENAI_MODEL` | `gpt-4.1-mini` | |

```sh
export OPENAI_API_KEY=sk-your-key-here
export OPENAI_BASE_URL=https://api.openai.com/v1   # optional
export OPENAI_MODEL=gpt-4.1-mini                   # optional
dotnet run --project BankingCli
```

## Example inputs

- `Move four dollars from savings to chequing`
- `Put $20 in savings`
- `Take $5 out of checking`
- `What's my savings balance?`
- `deposit savings 10` / `transfer savings chequing 4` / `balance` (original commands)

## Original commands

| Command | Effect |
| --- | --- |
| `deposit <account> <amount>` | Add money to an account. |
| `withdraw <account> <amount>` | Take money out of an account. |
| `transfer <from> <to> <amount>` | Move money between accounts. |
| `balance [account]` | Show one balance or both balances when the account is omitted. |
| `help` or `?` | Show the command list. |
| `exit` | End the session. |

Accounts are `chequing` and `savings`. Amounts are positive, with up to two decimal places, such as `5` or `5.25`. Withdrawals and transfers require sufficient funds, and transfers require two different accounts. A failed command leaves both balances as they were.

## How it works

1. Every line goes to the simulator first. Its output is printed as-is, unless it is `ERROR UNKNOWN_COMMAND` or `ERROR INVALID_ARGUMENTS`.
2. Only those two errors (probably English) go to the model, which fills a strict JSON schema: an operation from a fixed list, accounts, amount, or a clarifying question.
3. If anything is missing or unclear, or the amount doesn't appear in what the user typed, the user gets a question and nothing runs. Otherwise the app builds one command string itself.
4. The simulator executes that command exactly once.
5. A second call rewrites the result as a reply. The reply must contain every dollar amount from the simulator output and no amounts other than those and the requested one.
6. If the reply call fails or the check fails, the raw simulator output is printed. The command is not run again.

## Checks I ran

All four ran with a real OpenAI key and the default model (`gpt-4.1-mini`). Transcripts are copied from the terminal.

**1. Successful transfer.** Deposit with a command, transfer in English, confirm with `balance`.

```text
bank> deposit savings 10
DEPOSIT $10.00 TO savings | savings=$10.00
bank> Move four dollars from savings to chequing
I transferred $4.00 from your savings to your chequing account. Your new savings balance is $6.00 and your chequing balance is $4.00.
bank> balance
chequing=$4.00 | savings=$6.00
```

**2. Needs clarification.** Ask for a transfer with no amount and no source account, answer the question, then check the balance.

```text
bank> Move some money to chequing
Please specify the amount and from which account to move the money to chequing, for example 'move $5 from savings to chequing'.
bank> $5 from savings
Please specify what you'd like to do with $5 from savings, for example, 'withdraw $5 from savings' or 'transfer $5 from savings to chequing'.
bank> balance
chequing=$4.00 | savings=$6.00
```

Requests stand alone, so the follow-up gets another question rather than acting. Before a fix, this follow-up withdrew $5 (see How I worked).

**3. Failure case: insufficient funds.** Withdraw more than chequing holds, then check the balance.

```text
bank> Withdraw $50 from chequing
You don't have enough money in your chequing account to withdraw $50. Your current balance is $4.00.
bank> balance
chequing=$4.00 | savings=$6.00
```

**4. Clarification, then a decline.** Ask for $20 with only $15 in chequing and no source account named, then send the full request.

```text
bank> deposit chequing 15
DEPOSIT $15.00 TO chequing | chequing=$15.00
bank> move 20 bucks to savings
Please specify which account to move the $20 from. For example, 'move 20 from chequing to savings'.
bank> move 20 bucks from chequing to savings
The transfer didn't happen because your chequing account only has $15.00, which isn't enough to move $20.00.
bank> balance
chequing=$15.00 | savings=$0.00
```

The model never sees balances; the simulator decides there isn't enough money, and the reply explains its decision.

**API failure.** Running with `OPENAI_API_KEY=sk-invalid`, an English request prints "Sorry, I couldn't reach the assistant just now, so nothing was changed. You can still use commands - type help to see them." Balances are unchanged.

**Automated tests.** 45 xUnit tests, no key needed (`dotnet test`). They show that the starter's example session is byte-identical and makes zero API calls; that a clarifying question, a missing detail or an amount the customer never gave changes no balance; that when the reply call fails, the raw output is printed and the command ran exactly once; and that the amount check rejects a reply with a wrong, missing or under-repeated amount.

## Known limits

- Only chequing and savings exist (the starter's simulator). Requests naming other accounts ("my TFSA", "my vacation account") get a clarifying question.
- Amounts written without `$` in a reply ("6 dollars") aren't checked. The prompt asks for `$X.XX`.
- The app checks that the amount came from the customer: if they wrote digits, the model's amount must match one of them, optionally times a following "hundred", "thousand", "grand" or "k" ("2 hundred", "5k"). Otherwise the request must contain a number word ("four", "dozen", "grand"). The word's *value* isn't checked, so a model turning "four" into 5 would get through (the reply states the amount moved). "A dollar" or "a buck" gets a question asking for a number.
- Requests are standalone: there is no memory, so a clarifying question asks for the full request again.
- Wrong-arity original commands such as `withdraw chequing` now get a question instead of `ERROR INVALID_ARGUMENTS`.
- Three-word English that parses like a command (`deposit twenty dollars`, `balance please`) gets the starter's error (`ERROR INVALID_ACCOUNT`) instead of going to the model.

## How I worked

- **Tools.** Claude Code (Claude Opus 5.5) as a coordinator I talked to. More Claude Code (Opus 5.5) agents ran in separate terminal tabs (the Rex terminal multiplexer) as an investigator and as implementors. OpenAI Codex CLI (gpt-6-luna) was an independent reviewer.
- **Decisions.** For each architecture decision, the coordinator laid out 2–3 options with trade-offs and a recommendation, and I chose. There were 7 decisions, recorded in `docs/DESIGN.md`. I approved a design doc, then an implementation plan.
- **Cold read before coding.** A fresh agent read the design doc and caught a bug: the amount check would have rejected a good decline ("you can't withdraw $50…"), because $50 isn't in the simulator output. I changed the rule to also allow the command's own amount.
- **Implementation.** Implementors worked test-first, one task each. Agents didn't commit: the coordinator ran the tests and read the diff before each commit.
- **Rejected.** The AI recommended inferring the other account for "Move $5 to chequing", since only two accounts exist. I rejected that: a real member can have several savings accounts, so a transfer must name both accounts or the app asks. I verified it with a live run ("Move $5 to chequing" → clarifying question, balances unchanged) and an app-side guard test (`MissingRequiredField_AsksForDetail_NoChange`).
- **Accepted.** Codex found the amount check compared sets, so a repeated amount could be stated once and pass (moving all $4.00: the amount and the new balance are both $4.00). I verified it by first writing a failing test that reproduced it (`AmountCheck_RequiresRepeatedAmountsEachTime`), fixed it, then re-ran live requests to confirm no good replies were rejected.
- **Found live.** Answering a clarifying question with "$5 from savings" withdrew $5, because requests are standalone and the model guessed the operation. No test caught it. I fixed the prompt (no stated operation → ask; questions ask for the full request, with an example) and verified with repeated live runs that balances never changed.
- **Revisited a decision.** I first accepted "only the prompt stops the model inventing an amount" as a known limit. While reviewing the README I changed my mind: for a banking app, the app itself should enforce it. I added a local check (no extra API calls) that the amount appears in the customer's words, test-first ("a few dollars" → 3 and "20 bucks" → 25 are blocked). Then I measured it live. The prompt already caught every truly vague request ("the usual amount", "enough for coffee"), so the check is insurance in case the model or prompt changes, not a fix for an observed failure. It also had a cost: my first version wrongly blocked "2 hundred", "5k" and "a dozen dollars". I added multiplier support with tests and re-ran the live set to confirm those now work while vague amounts are still refused.
- **Model choice.** I compared gpt-4o-mini, gpt-4.1-mini and gpt-5-mini on the same 10 requests. All were correct; I picked gpt-4.1-mini (about 1 s per request vs about 7 s for gpt-5-mini).
- **See only my changes:** `git diff $(git rev-list --max-parents=0 HEAD) HEAD`.
