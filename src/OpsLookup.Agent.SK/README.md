# Ticketing AI Assistant — Semantic Kernel agent

A second agent for the same data API, written in C# on Semantic Kernel instead of
Python on LangGraph. It exists alongside the Python agent, not instead of it, so the
two can be run on different ports and compared.

## What is the same

Deliberately, so a difference in an answer points at the framework rather than at
incidental wording:

- The same two tools, `get_contract` and `get_attorney`, with the same parameters
  and the same "no SQL, no table names, no free-text filters" boundary.
- The same system prompts, verbatim from the Python agent's `graph/build.py`,
  including the Arabic wording (`الوكالة`, `الرقم الوطني الموحد`).
- The same contract on `/api/chat` and `/api/health`, so the Vue client drives
  either without changes.
- The same per-turn service token. .NET mints it, the agent holds it for one turn
  and drops it, and nothing here caches it.
- The same social ID rule: 10 digits starting with 1 or 2.
- SQLite conversation history keyed by `ticketId`, standing in for LangGraph's
  checkpointer.

## What is different, and why

**No cold start.** The Python agent pays 60–120s importing `langgraph` after every
recycle. That was the main argument for hosting as a Windows Service, and it does
not apply here — the process is usable as soon as Kestrel binds. The Docker image is
496 MB with no interpreter, no virtualenv, and no ICU to work around.

**The tool loop is written out** rather than delegated to
`FunctionInvokingChatClient`. SK rejects `FunctionChoiceBehavior.Auto` without a
`Kernel`, and supplying one makes the connector invoke the tools itself — which
loses two things this application reports: how many tools ran, and whether any of
them *failed*. So the counts are reconstructed by snapshotting function-call ids
before each model call. That is why `toolCalls` and `lookupFailed` are correct here.

**A fresh `Kernel` per turn.** It is only a function registry, so it costs nothing,
and it lets the bearer token live in a constructor field. A shared plugin would
need ambient state to carry a per-turn token, and ambient state is how two
concurrent tickets end up using each other's credentials.

**`ready` replaces `pythonReady`.** There is no interpreter to warm up. The useful
distinction is now `apiReachable`, which is a real probe rather than a flag.

## The Zen gateway needs two headers

`https://opencode.ai/zen/go/v1` rejects a request that does not identify itself,
returning `MissingSessionID`. A generic OpenAI client sends neither, so `Program.cs`
attaches `x-opencode-session` and a `User-Agent` explicitly. The session id is
stable for the process lifetime — the gateway uses it for routing and prompt
caching, and a fresh id per request defeats the cache entirely.

## Running it

```powershell
# the data API, Development, on 5250
dotnet run --project src\OpsLookup.Api

# this agent, on 5101
$env:Lookup__BaseUrl        = "http://127.0.0.1:5250"
$env:Jwt__ServiceSecret     = "<the API's>"
$env:Model__ApiKey          = "<LongCat key>"
$env:Conversation__DatabasePath = "C:\Temp\conversations.sqlite"
dotnet run --project src\OpsLookup.Agent.SK
```

Then <http://localhost:5101>. The Arabic UI is the default; the toggle is in the
header.

## Docker

```powershell
$env:JWT_SIGNING_KEY    = "<32+ chars>"
$env:JWT_SERVICE_SECRET = "<shared with the API>"
$env:MSSQL_PASSWORD     = "<SQL login>"
$env:MODEL_API_KEY      = "<LongCat key>"
docker compose up --build
```

**The compose stack does not reach the SQL Server on this machine, and the reason
is not fixable in the application.** The local SQL Server Express reports
`IsIntegratedSecurityOnly = 1` — Windows authentication only. A container has no
Windows identity for it, and a SQL login can be created against it but never used.
Verified: a login created for this purpose fails with "Login failed for user" over
TCP. The API container starts and reports healthy, then returns 500 from the driver
on every data request, which reads as an application fault.

Either switch that instance to mixed mode (admin, and it weakens auth), point at a
different server that allows SQL logins, or run SQL Server in a sibling container
with SQL auth enabled. The compose file says the same thing in place.

The IIS deployment is unaffected: the app pool identity is a Windows account and
uses Integrated Security, which that instance accepts.

## Verification

33 behavioural checks pass against a live data API (`sk_check.py`), covering both
languages, approved and flagged agencies, multi-turn context, a malformed
identifier, a missing identifier, an unknown locale, thread clearing, and request
validation.

One assertion in that suite is worth reading before trusting it: for a malformed
identifier the correct behaviour is **not** to call the tool at all — the prompt
tells the model to ask instead — so `lookupFailed` must stay `false`. An earlier
version of the test asserted the opposite and failed against correct code.

## Layout

```
Agents/SystemPrompts.cs   the two prompts, ported verbatim
Agents/TicketAgent.cs     the loop, history replay, tool accounting
Plugins/LookupPlugin.cs   the two tools; token passed in, never held
Data/LookupClient.cs      typed client for the data API
Data/ConversationStore.cs SQLite history, WAL, one connection per call
Services/ServiceTokenClient.cs  mints a token per turn, no cache
```