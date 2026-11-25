# Banking CLI Assistant — Design

Status: Approved 2026-11-24; updated after implementation.

## Intent

Mainstreet Credit Union's take-home for a Full Stack Software Developer role (due Thu 2026-11-26, 5pm).
The starter (`dpo007/banking-cli-starter@b7eb68e`) is a .NET 10 console app with in-memory chequing and
savings accounts and a strict command syntax. We extend it so a person can type everyday English
("Move four dollars from savings to chequing") and get a conversational reply, using an
OpenAI-compatible API.

The reviewers will judge clarity and judgment as much as function: the solution must be well
documented and **not over-engineered**.

## Goals

1. Plain-English requests for all four operations: deposit, withdraw, transfer, balance (one or both accounts).
2. Each English request maps to **exactly one** existing command, executed by the unmodified simulator.
3. A missing or unclear detail produces a clarifying question and **no balance change**.
4. Original commands (`deposit`, `withdraw`, `transfer`, `balance`, `help`, `?`, `exit`) keep working.
5. Simulator results are rewritten as conversational replies that preserve outcome, amounts, accounts and balances — including declined transactions.
6. If the reply call fails, print the simulator's original output **without re-running the command**.
7. README: build/run, configuration (endpoint, model, key), example inputs, three documented checks
   (successful transfer, clarification, a failure case), and "How I worked" (AI tools used, one suggestion
   accepted/changed/rejected and how it was verified).

## Non-goals

- Multi-turn memory. Each request stands alone; a clarifying question asks the user to retry with the detail.
- English mapping to `help` / `exit`. The AI path covers the four banking operations only.
- Streaming, retries, provider-specific features, logging frameworks, DI containers, config files.

## Anti-goals (would be failure even if it "works")

- Any path where money moves on a guessed amount or account.
- A friendly reply that misstates a number.
- A command executed twice.
- Valid original commands that call the API or produce different output.
- API keys in the repo.
- Abstractions with one implementation (interfaces, factories, provider layers).

## Constraints

- .NET 10, runnable solution (`BankingCli.sln`).
- OpenAI-compatible API, configurable endpoint/model/key. Developed against the OpenAI API.
- `BankSession.cs` stays unchanged — the simulator is the single source of truth and validation.
- Starter `Program.cs` hides the prompt when input is redirected "so it can be compared line by line";
  that property is preserved.

## Decisions (approaches considered)

| # | Decision | Options considered | Chosen | Why |
|---|---|---|---|---|
| 1 | API used during development | Claude via Anthropic's OpenAI-compatible endpoint; **OpenAI API key**; ask the recruiter for a key | OpenAI key | Matches what reviewers most likely run. |
| 2 | Command vs. English routing | Fall back to AI on `UNKNOWN_COMMAND` only; **on `UNKNOWN_COMMAND` + `INVALID_ARGUMENTS`**; on any parse error; AI-first | `UNKNOWN_COMMAND` + `INVALID_ARGUMENTS` | Simulator-first is safe because every `ERROR` leaves balances untouched. Catches most English (sentences have more words than any command). Keeps the `INVALID_ACCOUNT` / `INVALID_AMOUNT` codes byte-identical. Only deliberate change: wrong-arity commands like `withdraw chequing` now get a clarifying question instead of `ERROR INVALID_ARGUMENTS`. |
| 3 | How the model returns its interpretation | Plain-text line; **structured JSON (strict schema) with fields**; function calling | Structured fields | The model *chooses* from enums, the app *builds* the command string, the simulator *validates*. The model never writes raw command text. |
| 4 | API client | Official `OpenAI` NuGet; **`HttpClient` + `System.Text.Json`**; `Microsoft.Extensions.AI` | `HttpClient` | Zero dependencies, about 50 lines, wire format visible, explicit short timeout so the fallback is fast, fakeable in tests via `HttpMessageHandler`. |
| 5 | Keeping replies truthful | Trust the prompt; **prompt + dollar-amount check**; show reply + raw receipt | Prompt + check | Every `$` amount in the simulator output must appear in the reply. The reply may contain only those amounts plus the command's own amount (compared as decimals, so `$4` == `$4.00`). On failure, treat as an API failure and print the raw output. |
| 6 | Testing | Manual only; **small xUnit project + 3 manual checks**; shell diff script | xUnit + manual | Proves the rules a reviewer can't easily eyeball (no double execution, amount check, byte-identical commands) without an API key. |
| 7 | Repo | GitHub fork; new repo keeping starter history; **new repo, starter as one commit** | New repo, single starter commit | Not discoverable via the starter's Forks list. The first commit is the starter verbatim (message names the source repo and SHA), so `git diff <first-commit>..main` shows only our work. |

## Design

### Request flow

```
input
  │
  ▼
BankSession.Execute(input)                      ← unchanged simulator
  │
  ├─ output is NOT "ERROR UNKNOWN_COMMAND" / "ERROR INVALID_ARGUMENTS"
  │     → print output exactly as the starter does (exit handled as before)
  │
  └─ otherwise (probably English)
        ├─ no API key configured → print the simulator output unchanged (starter behavior)
        │
        ├─ Call 1 — interpret (strict JSON schema)
        │     API error / timeout / bad JSON → print "assistant unavailable, use commands (type help)"; nothing ran
        │     action = clarify, or required field missing → print the question; nothing ran
        │     otherwise → app builds ONE command string
        │
        ├─ BankSession.Execute(command)        ← exactly once
        │
        └─ Call 2 — reply (plain text)
              success AND amount check passes → print reply
              anything else                   → print the simulator output from the step above (no re-run)
```

### Call 1 — interpret

- Input: system prompt plus the user's text. **No balances are sent.** The model can't resolve
  "half my savings" and must ask instead.
- Output schema (OpenAI strict mode: all fields required, nullable where noted, `additionalProperties: false`):
  - `action`: `deposit | withdraw | transfer | balance | clarify`
  - `account`: `chequing | savings | null` (deposit, withdraw, optional for balance)
  - `from`, `to`: `chequing | savings | null` (transfer)
  - `amount`: number or null
  - `question`: string or null (set when `action = clarify`; also used for off-topic requests, where it
    says what the assistant can do and reminds the user that `exit` leaves)
- Prompt rules: never invent an amount or account. **A transfer must name both accounts.** "Move $5 to
  chequing" gets a clarifying question even though only one other account exists today. The rule
  shouldn't depend on there being exactly two accounts, since a real member may have several. The
  app-side guard enforces the same thing: `from` or `to` null → ask. A `balance` request with no account
  shows both, as the starter's `balance` does. "checking" means chequing. Words-to-numbers is fine
  ("four dollars" → 4). Vague amounts ("some", "everything", "half") → clarify. **The operation must be
  stated** ("$5 from savings" names no operation → clarify). **Messages stand alone:** the model can't see
  earlier messages, so a reply to a clarifying question is not treated as an answer; questions ask for the
  full request again, with an example. **Other account names** ("my TFSA", "my vacation account") →
  clarify, saying only chequing and savings are available. The model must not map them onto an existing account.
- **App-side guard:** if a required field for the action is null, the app asks a generic clarifying
  question itself. Money never moves on an incomplete interpretation, even if the model misbehaves.
- **Amount grounding check (added after implementation; originally an accepted limit).** Before
  executing, the app checks that the model's amount came from the customer's text, with no extra API
  call. If the text contains digits, the amount must equal one of those numbers, or that number times a
  following multiplier (`hundred` ×100; `thousand`, `grand`, `k` ×1000). If there are no digits, the text
  must contain a number word (one–twenty, tens, hundred, thousand, dozen, grand). Otherwise the app
  asks for the amount as a number and nothing runs. Remaining limit: a number word's *value* isn't
  checked ("four" → 5 passes). Full number-word parsing was judged out of proportion. "A dollar" or
  "a buck" gets a question.
- The app formats the amount with `CultureInfo.InvariantCulture`. A bad value (negative, 3 decimals) is
  rejected by the simulator and explained in the reply.

### Call 2 — reply

- Input: the user's original text, the command run, and the simulator output. **The simulator output
  contains balances**, which the reply must state, so balances do reach the API in this call (never in
  Call 1). The decision about funds is always the simulator's.
- Prompt: one or two sentences of everyday English. Keep every dollar amount exactly as written.
  `ERROR …` means the bank declined and nothing changed; explain why in plain words (insufficient funds,
  same account, invalid amount, balance limit).
- Amount check: a regex extracts `$`-amounts from both texts and parses them as `decimal`.
  - **Required:** every amount in the simulator output appears in the reply, as many times as it appears
    there (a transfer of all $4.00 shows $4.00 twice, so the reply must state it twice).
  - **Allowed:** those amounts plus the amount in the executed command. Declines like
    `ERROR INSUFFICIENT_FUNDS | chequing=$0.00` don't contain the requested amount, and a good reply
    ("you can't withdraw $50…") needs to quote it.
  - Anything else → fall back to the raw output.
  - The prompt requires every amount to be written as `$X.XX`. Known gap: an amount written without
    `$` ("6 dollars") isn't checked. The README documents this.

### Configuration

Environment variables, using the standard OpenAI names:

| Variable | Default | Notes |
|---|---|---|
| `OPENAI_API_KEY` | none | Unset → commands-only mode; behaves exactly like the starter. A one-line notice goes to **stderr** at startup when interactive. |
| `OPENAI_BASE_URL` | `https://api.openai.com/v1` | Any OpenAI-compatible endpoint. Must be `https://`; plain `http://` only for localhost. Otherwise the app exits at startup with a configuration error, since the key and balances travel in the requests. |
| `OPENAI_MODEL` | `gpt-4.1-mini` | Chosen after comparing gpt-4o-mini, gpt-4.1-mini and gpt-5-mini on the same requests: all correct, gpt-4.1-mini fastest. |

HTTP timeout: 30 s. No retries, because a failure falls back immediately. English input longer than 500
characters is refused before any API call (cost, hidden instructions); original commands are unaffected.

### Code layout

```
BankingCli.sln
BankingCli/
  BankSession.cs        unchanged
  Program.cs            loop: read line → assistant.Handle(line) → print; prompt hiding preserved
  Assistant.cs          routing, prompts, schema, command building, amount check
  OpenAiChat.cs         HttpClient wrapper: CompleteAsync(messages, schema?) → string
  BankingCli.csproj     + InternalsVisibleTo BankingCli.Tests
BankingCli.Tests/
  AssistantTests.cs     xUnit, fake HttpMessageHandler, no key needed
  OpenAiChatTests.cs    request shape and failure handling of the HTTP client
  FakeChatHandler.cs    queued fake responses; counts API calls
docs/DESIGN.md          this document
README.md               rewritten per Goal 7 (starter content kept where still true)
LICENSE                 MIT, original notice kept
```

### Automated tests (no key)

1. The starter README's example session produces byte-identical output.
2. `INVALID_ACCOUNT` / `INVALID_AMOUNT` inputs never call the API. `UNKNOWN_COMMAND` / `INVALID_ARGUMENTS` do.
3. A clarifying interpretation leaves both balances unchanged.
4. A missing required field (model returned `transfer` with `amount: null`) leaves balances unchanged.
5. Reply call fails → raw simulator output is printed and the balance changed exactly once.
6. A reply with a wrong amount is rejected → raw output is printed.
7. No key → English input prints the starter's output.

### Manual checks (real key, transcripts in README)

1. Successful transfer ("Move four dollars from savings to chequing" after a deposit).
2. Clarification ("Move some money to chequing" → question, balances unchanged via `balance`).
3. Failure case ("Withdraw $50 from chequing" with insufficient funds → friendly decline; balances unchanged).

## Development workflow

- **Hub:** the candidate makes every decision; the coordinator (Claude Code in the main pane) explains
  options and writes briefs.
- **Agents in Rex tabs:** investigators (read-only Claude Code), one implementor at a time (Claude Code,
  edits allowed), and a reviewer (Codex CLI, `codex review`) once per milestone. Each agent gets a written
  brief and returns a written report; briefs, reports and working notes stay outside the repo.
- Repo: public GitHub repo `sam-michaels/banking-cli-assistant`, created with `gh` after this doc was
  approved. API: Chat Completions.
- Small commits straight to `main`. Commits carry the candidate's name only, with no AI attribution lines.
  The README's "How I worked" section discloses AI use, as the assignment requires.

## Left to the builder

- Exact prompt wording, class/method names, and internal structure within the layout above.
- The reply wording style, within "1–2 sentences, everyday English".
- Test names and the fake-handler shape.
