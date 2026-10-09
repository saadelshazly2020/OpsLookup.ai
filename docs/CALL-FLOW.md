# Call flow: UI → .NET → Python → .NET API → SQL Server

Exactly what happens between a member pressing Enter and an answer appearing, and
why each hop is built the way it is. Companion to
`src/OpsLookup.NetCorePythonNetApp/README.md`, which covers running and
troubleshooting.

Four processes are involved. Only one of them is Python's web server — there
isn't one.

| Process | Port | Role |
|---|---|---|
| Browser | — | Vue page |
| `OpsLookup.NetCorePythonNetApp` | 5100 | Serves the page, embeds Python, hosts the graph |
| *(same process)* | — | The embedded CPython interpreter and the LangGraph agent |
| `OpsLookup.Api` | 5250 | Owns SQL Server. Separate process, bearer-token protected |

---

## 1. The whole chain

```
┌──────────┐
│ Browser  │  Vue 3 + Vite, built into wwwroot
└────┬─────┘
     │ POST /api/chat  { ticketId, message }
     ▼
┌────────────────────────────────────────────┐
│ ASP.NET Core  Program.cs                   │
│   • JSON → ChatRequest                     │
│   • _queue.Add(...)   ──────────────┐      │
└───────────────────────────────────────┼──────┘
                                       │  BlockingCollection<Action>
                                       ▼
                       ┌───────────────────────────────┐
                       │ python-worker  (dedicated Thread)
                       │   • owns PythonEngine         │
                       │   • acquires Py.GIL()          │
                       │   • InvokeString(host.run_turn)│
                       └───────────────┬───────────────┘
                                       │  in-process, GIL held
                                       ▼
                       ┌───────────────────────────────┐
                       │ host.run_turn()               │
                       │   loop.run_until_complete()   │
                       │     └─ graph.ainvoke()       │
                       │          ├─ llm  ───────────────┼──▶ LongCat / OpenCode Zen
                       │          └─ action (tool)      │
                       │               get_contract()   │
                       └───────────────┬───────────────┘
                                       │  HTTP + Bearer token
                                       │  (fresh httpx client per turn)
                                       ▼
                       ┌───────────────────────────────┐
                       │ OpsLookup.Api   :5250          │
                       │   GET /api/contracts          │
                       │   ContractRepository          │
                       │   EF Core ─▶ SQL Server       │
                       └───────────────────────────────┘
```

Steps 1–9 below follow that diagram top to bottom.

---

## 2. Step by step

### Step 1 — Browser posts the question

`ClientApp/src/App.vue` → `send()` → `services/api.ts:sendChat()`:

```ts
fetch('/api/chat', {
  method: 'POST',
  headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ ticketId, message }),
})
```

Same origin as the page, so no CORS. `ticketId` comes from `localStorage`
(`opslookup.netcore.threadId`) and is stable across turns, which is what gives the
conversation its memory.

A "thinking" indicator is shown, and the composer is disabled. The badge logic in
`ChatMessage.vue` depends on `toolCalls`:

```
toolCalls > 0  →  "Looked up N times in the database"
toolCalls = 0  →  no badge  ← the answer was NOT verified against data
```

### Step 2 — ASP.NET Core accepts the request

`Program.cs` maps the endpoint and binds the body:

```csharp
app.MapPost("/api/chat", async (ChatRequest request, PythonBridge bridge, CancellationToken ct) => …)
public sealed record ChatRequest(string TicketId, string Message, string? OperatorId);
```

If `bridge.IsReady` is false it returns **503** immediately rather than queueing —
a request that arrives during the 60–120s warm-up should not pile up.

### Step 3 — The call is queued, not executed

`PythonBridge.ChatAsync` **never touches Python itself**. It calls `RunAsync`,
which appends a closure to a `BlockingCollection<Action>` and returns a
`TaskCompletionSource`:

```csharp
public Task<ChatOutcome> ChatAsync(string ticketId, string message, string? operatorId)
    => RunAsync(() => ParseOutcome(ticketId,
           InvokeString(_runTurn!, ticketId, message, operatorId ?? string.Empty)));
```

This indirection exists because of the rule in step 4. The ASP.NET thread that
handled the request returns immediately and awaits the task, so no request thread
is parked while Python works.

### Step 4 — One dedicated thread owns the interpreter

`WorkerLoop()` drains the queue. Before touching anything else it initialises the
engine on **its own thread**:

```csharp
_worker = new Thread(WorkerLoop) { IsBackground = true, Name = "python-worker" };

private void WorkerLoop()
{
    EnsureInterpreterInitialised();   // PythonEngine.Initialize() happens HERE
    ImportHost();
    foreach (var work in _queue.GetConsumingEnumerable()) { work(); }
}
```

> **Why a dedicated thread.** Python.NET only attaches the thread that called
> `PythonEngine.Initialize()`. A thread-pool thread calling `Py.GIL()` blocks
> forever — no exception, no log line, the request simply never returns. This was
> the single most expensive bug in the project, and it presented as a silent
> startup hang.

`EnsureInterpreterInitialised` sets three things:

| Setting | Value | Why |
|---|---|---|
| `Runtime.PythonDLL` | `C:\Python314\python314.dll` | pythonnet 3.x refuses to initialise without it. Found by matching `python3\d+\.dll` — `python3.dll` is only a forwarder. |
| `PythonEngine.PythonHome` | base install (from `pyvenv.cfg`) | A venv contains no DLL of its own. |
| `PythonEngine.PythonPath` | venv `site-packages`, then base `Lib`, `DLLs`, base, venv | Order matters: the venv must win so `langgraph` resolves from this app, but the base `Lib` is still needed for `encodings`. Omit it and you get `Failed to import encodings module`. |

`ImportHost` then prepends the Python folder to `sys.path` and binds four
functions:

```csharp
_host         = Py.Import("host");
_warmup       = _host.GetAttr("warmup");
_runTurn      = _host.GetAttr("run_turn");
_apiReachable = _host.GetAttr("api_reachable");
_diagnostics  = _host.GetAttr("diagnostics");
```

> The `sys.path` insert uses explicit `PyObject` calls, not `dynamic`. A Python
> list has no `Contains` method — that is LINQ — so dynamic member lookup raises
> `AttributeError` instead of falling back to `__contains__`.

### Step 5 — The GIL is taken for the turn

```csharp
_queue.Add(() =>
{
    using var gil = Py.GIL();                      // acquired AND released here
    completion.TrySetResult(call());                 // on this same thread
});
```

Two rules, both learned the hard way:

- The GIL is **thread-bound**. An `await` can resume on a different thread, which
  makes a `using (Py.GIL())` spanning an await release on the wrong thread and
  corrupt state. That is why `ChatAsync` returns a task from `RunAsync` instead
  of awaiting anything inside the GIL block.
- It is acquired and released in **one synchronous block**. Never across an await.

Holding it for the whole turn is safe: while a turn waits on the model or on SQL
Server, socket I/O releases the GIL, which is what lets two concurrent requests
both complete. Verified: two concurrent turns finished in 23s, both with
`toolCalls: 1`.

### Step 6 — .NET calls into Python

`InvokeString` marshals arguments and unwraps the result:

```csharp
private static string InvokeString(PyObject function, params object[] args)
{
    var pyArgs = args.Select(ToPy).ToArray();      // string → PyString, int → PyInt, …
    try
    {
        using var result = function.Invoke(pyArgs);
        return result.As<string>() ?? string.Empty;
    }
    finally { foreach (var arg in pyArgs) arg.Dispose(); }   // every PyObject disposed
}
```

Every `PyObject` is disposed explicitly. The GC finaliser only runs when some
later call reacquires the GIL, so relying on it leaks references.

The contract is **a JSON string**, not a rich object. Python returns
`{"threadId":…, "reply":…, "toolCalls":…}` or `{"error":…}`, and
`ParseOutcome` maps that to a `ChatOutcome` record.

### Step 7 — `host.run_turn` drives the graph

`Python/host.py` is the only file that exists solely for this bridge. The rest of
`Python/` is a verbatim copy of `OpsLookup.Graph`.

```python
def run_turn(thread_id, message, operator_id="") -> str:
    failure = warmup()                       # cheap no-op after the first call
    if failure:
        return json.dumps({"error": failure})
    try:
        payload = _get_loop().run_until_complete(_run_turn(thread_id, message, operator_id))
    except Exception as exc:
        return json.dumps({"error": _describe(exc), "errorType": type(exc).__name__})
    return json.dumps(payload, default=str)
```

`_run_turn` is the coroutine, and it is the only place a turn's resources exist:

```python
async def _run_turn(thread_id, message, operator_id) -> dict:
    async with AsyncExitStack() as stack:
        client = LookupApiClient(…)                    # bearer token + X-Operator-Id
        stack.push_async_callback(client.aclose)

        checkpointer = await stack.enter_async_context(
            AsyncSqliteSaver.from_conn_string(_checkpoint_path(settings)))

        tools = build_tools(client)
        graph = build_graph(build_llm(settings, tools), tools, checkpointer)

        result = await graph.ainvoke(
            {"messages": [("user", message)], "ticket_id": thread_id, …},
            {"configurable": {"thread_id": thread_id}, "recursion_limit": 10})
```

Three deliberate choices:

- **A fresh graph and checkpointer per turn.** Nothing is shared, so concurrent
  turns cannot interfere.
- **One shared event loop** (`_get_loop`). A fresh loop per turn — the obvious
  `asyncio.run` — outlives httpx connection pools, and reusing a pool against a
  closed loop fails mid-call with `RuntimeError: Event loop is closed`.
- **`_checkpoint_path` pins the SQLite file** to this app's Python directory. A
  relative path would resolve against the process working directory, so two apps
  started from different folders could share one database.

### Step 8 — The graph decides whether to call the API

`Python/graph/build.py` is three nodes and one conditional edge:

```python
builder.add_node("llm", llm_node)
builder.add_node("action", ToolNode(tools))
builder.add_edge(START, "llm")
builder.add_conditional_edges("llm", route_after_llm, {"action": "action", END: END})
builder.add_edge("action", "llm")

def route_after_llm(state):
    last = state["messages"][-1]
    return "action" if getattr(last, "tool_calls", None) else END
```

| Model emits | Route | Meaning |
|---|---|---|
| tool call | → `action` → `llm` | needs data |
| plain text | → `END` | done — **or** asking the member a question |

A clarifying question and a final answer travel the same path. Both are text with
no tool call, and both become the reply. There is no "needs_info" branch because
the graph does not need one.

`recursion_limit: 10` caps a turn. Without it a model handed a confusing response
retries indefinitely.

### Step 9 — The tool calls the .NET API

`Python/tools/contracts.py` declares exactly one tool:

```python
StructuredTool(
    name="get_contract",
    args_schema=GetContractInput,     # social_id: int, name: str | None
    coroutine=get_contract,           # explicit — see note
)
```

> `coroutine=` is set explicitly. `StructuredTool.from_function()` does not
> reliably detect an async closure and silently wraps it as sync, which creates
> the coroutine without ever awaiting it — the API is never called and the tool
> returns nothing.

`Python/tools/api_client.py` does the HTTP:

```python
async def get_contracts(self, social_id: int, name: str | None = None):
    params = {"socialId": str(social_id)}
    if name:
        params["name"] = name
    response = await self._client.get("/api/contracts", params=params)
    if response.status_code == 401:  raise LookupApiError("…API_TOKEN matches Jwt:SigningKey")
    if response.status_code == 400:  raise LookupApiError(_problem_detail(response))
    return response.json()
```

Headers were set once when the client was constructed:
`Authorization: Bearer <API_TOKEN>` and `X-Operator-Id`.

`get_contract` catches `LookupApiError` and returns it as **text** rather than
letting it propagate:

```python
except LookupApiError as exc:
    return json.dumps({"error": str(exc)})
```

That is deliberate: the model then explains the failure to the member in plain
language. Seen in testing — a stale token produced *"the system returned an
authentication error… this is a technical configuration issue on our side"*
rather than a stack trace.

### Step 10 — OpsLookup.Api reaches SQL Server

A **separate process**, deliberately. Python holds no database driver, so there is
no path by which a generated string becomes SQL.

`ContractEndpoints.cs`:

```csharp
if (!SocialIdRule.IsValid(socialId))
    return TypedResults.Problem(title: "Invalid social ID", detail: SocialIdRule.Description, …);

var results = await repository.GetBySocialIdAsync(socialId, name, cancellationToken);
return TypedResults.Ok(results);
```

`ContractRepository.cs` projects through the navigation property so EF generates
the JOIN:

```csharp
IQueryable<Party> parties = db.Parties.AsNoTracking().Where(p => p.SocialId == socialId);
if (!string.IsNullOrWhiteSpace(partyName))
    parties = parties.Where(p => p.Name.Contains(partyName));   // parameterized LIKE
return await parties.OrderBy(p => p.Contract.Code)
    .Select(p => new ContractResult(p.Contract.Code, p.SocialId, p.Name, p.Contract.Name))
    .ToListAsync(cancellationToken);
```

> `.OrderBy()` must come **before** `.Select()`. Applied after, EF tries to order
> by the whole `new ContractResult(…)` expression and fails with *"The LINQ
> expression could not be translated"*.

Response:

```json
[{ "contractCode": 100234, "socialId": 1234567890,
   "partyName": "A Nasser", "contractName": "Annual Maintenance Agreement" }]
```

That JSON becomes the tool result the model reads.

### Step 11 — Back out

`host.run_turn` counts the tool calls made anywhere in the transcript, which is
how `toolCalls` reaches the badge:

```python
tool_calls = sum(len(getattr(m, "tool_calls", []) or []) for m in messages)
```

---

## 3. Payloads at each boundary

| # | Boundary | Shape |
|---|---|---|
| 1 | Browser → .NET | `{ ticketId, message }` |
| 2 | .NET → Python | `run_turn(ticketId: str, message: str, operatorId: str) -> str` |
| 3 | Python → model | messages + one tool schema |
| 4 | Tool → .NET API | `GET /api/contracts?socialId=&name=` + `Bearer` |
| 5 | .NET API → Python | `[{ contractCode, socialId, partyName, contractName }]` |
| 6 | Python → .NET | `{ threadId, reply, toolCalls }` or `{ error }` |
| 7 | .NET → Browser | same, as `ChatOutcome` |

Only boundary 2 crosses the language boundary, and it carries **strings and
numbers only** — no complex objects to marshal.

---

## 4. Failure paths

| Failure | Detected at | Result |
|---|---|---|
| Malformed social ID | `SocialIdRule`, in .NET | 400 → tool error text → model explains it |
| Expired `API_TOKEN` | `LookupApiClient` | 401 → same route |
| Model cannot call tools | nowhere — **silent** | `toolCalls: 0`, no badge. **Every answer is a guess.** |
| `MissingSessionID` | provider | mapped by `_describe()` to a readable message |
| Python still warming | `bridge.IsReady` | 503, page shows the warning pill |
| Worker thread dead | — | requests time out; check `Python/logs/progress.log` |

Every Python-side failure is caught and returned as JSON with an `error` key, so a
Python exception never becomes an HTTP 500 with an empty body.

---

## 5. Timing

Measured on this machine:

| Phase | Time |
|---|---|
| First start — `langgraph` import, embedded | **60–120s** |
| Subsequent starts | 5–15s |
| One turn, tool call + lookup + answer | 15–20s |
| Two concurrent turns | 23s total |

The embedded import is ~10× slower than standalone. It is a pythonnet cost, not
configuration. `/api/health` reports `pythonReady: false` until warm-up completes,
and the page works throughout.

---

## 6. Extending it

**Add a lookup.** Three edits, and SQL never leaves the .NET project.

1. `Python/tools/contracts.py` — a tool with typed parameters
2. `Python/tools/api_client.py` — the HTTP call
3. `OpsLookup.Api` — entity, repository method, endpoint

**Add a field to a response.** Entity property → `.Select(…)` in the repository →
`ContractResult`. Keep the projection explicit; returning entities would leak
columns.

**Change the schema.** `dotnet ef migrations add <Name>` against
`OpsLookup.Api`. The migration files are the schema history.

---

## 7. Where the invariants live

Four rules that each replaced a design that looked correct and failed silently.
They are commented at the code they govern, and repeated here because they are the
first thing to break when this code is edited.

1. **One thread owns Python.** pythonnet attaches only the thread that called
   `PythonEngine.Initialize()`. `Py.GIL()` anywhere else blocks forever, silently.
2. **The GIL is never held across an `await`.** It is thread-bound; an await may
   resume elsewhere.
3. **Nothing writes to `sys.stderr`.** Embedded streams are not reliably attached
   to the console, and a blocked write happens *with the GIL held*, stalling every
   `Py.GIL()` in .NET. Use `host._log` → `Python/logs/progress.log`.
4. **One event loop for the process.** `asyncio.run` per turn gives httpx pools a
   closed loop to be reused against.