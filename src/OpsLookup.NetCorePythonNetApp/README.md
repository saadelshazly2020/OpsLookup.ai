# OpsLookup.NetCorePythonNetApp

ASP.NET Core with the LangGraph agent **embedded** in the process via
Python.NET. No Python web server, no HTTP hop to one — the .NET app calls the
graph directly.

This app is self-contained. It shares no files with `OpsLookup.Api` or
`OpsLookup.Graph`: its own Python copy, its own virtualenv, its own `.env`, its
own checkpoint database. The only outside dependency is a **runtime HTTP call** to
`OpsLookup.Api` (port 5250), which owns SQL Server — Python must never hold a
database connection.

## Layout

```
Python/                  embedded agent — a verbatim copy of OpsLookup.Graph
  host.py                the ONLY new file: the bridge .NET calls
  config.py llm.py       copied verbatim
  graph/ tools/          copied verbatim
  .env                   this app's own values
  .venv/                 this app's own Python 3.14 environment
  logs/                  progress.log — see Troubleshooting
ClientApp/               Vite + Vue 3 + TypeScript (sources, committed)
wwwroot/                 ClientApp build output (generated, gitignored)
Services/PythonBridge.cs the .NET side of the bridge
Program.cs               endpoints + startup warm-up
```

## Running

Three processes. Start them in this order.

```powershell
# 1. the data API (OpsLookup.Api, port 5250)
cd ..\OpsLookup.Api ; dotnet run

# 2. build the page, once (or whenever ClientApp/src changes)
cd OpsLookup.NetCorePythonNetApp\ClientApp ; npm install ; npm run build

# 3. this app (port 5100)
cd .. ; dotnet run --urls http://localhost:5100
```

Then open **http://localhost:5100**.

Or `./start.ps1`, which checks the environment, builds the page, starts the app
and opens the browser.

### First start is slow

Importing `langgraph` inside an embedded interpreter takes **60–120 seconds**,
versus ~12s standalone. `/api/health` reports `pythonReady: false` until warm-up
finishes, and the page works throughout — it just waits. Later starts reuse the
OS file cache and are quicker. This is a pythonnet cost, not a configuration one.

## Endpoints

| Method | Route | Purpose |
|---|---|---|
| `GET` | `/` | The Vue chat page, from `wwwroot` |
| `GET` | `/api/health` | `{ status, pythonReady, apiReachable, startupError }` |
| `POST` | `/api/chat` | `{ ticketId, message, operatorId? }` → `{ threadId, reply, toolCalls }` |

`toolCalls` is the number of database lookups behind the reply. The page shows a
**"Looked up N times"** badge when it is greater than zero — an answer with no
badge was not verified against data, and that distinction is the whole point of
the app.

`ticketId` is the conversation thread. Reuse it for follow-up questions so the
checkpointer restores context.

## How the bridge works

```
browser ──POST /api/chat──▶ ASP.NET Core
                              │  queue
                              ▼
                      python-worker thread  (the only thread that touches Python)
                              │  Py.GIL()
                              ▼
                     host.run_turn(...)      asyncio.run on a persistent loop
                              │  tool call
                              ▼
                    GET localhost:5250/api/contracts  ──▶ SQL Server
```

Three decisions here were forced by how Python.NET actually behaves, and each
one replaced a design that looked reasonable and failed silently:

**All interpreter work runs on one dedicated thread**, and the engine is
initialised on that same thread. pythonnet only attaches the thread that called
`PythonEngine.Initialize()`; a thread-pool thread calling `Py.GIL()` blocks
forever. The symptom was a startup that hung with no error and no log output.

**Nothing is written to `sys.stderr`.** Embedded Python streams are not reliably
attached to the host console, and a blocked write there happens *with the GIL
held*, which stalls every `Py.GIL()` call in .NET. Logs go to
`Python/logs/progress.log` and are readable from outside while the host is stuck.

**Python keeps one event loop**, not `asyncio.run` per turn. A fresh loop per
turn outlives httpx connection pools, and reusing a pool against a closed loop
fails with `RuntimeError: Event loop is closed` mid-llm-call.

The GIL is acquired and released on one thread inside one synchronous block, and
never held across an `await`.

## Configuration

`appsettings.json`:

```json
"Python": {
  "Directory": "Python",
  "Venv": "Python/.venv",
  "StartupTimeoutSeconds": 180
}
```

| Key | Default | Notes |
|---|---|---|
| `Python:Directory` | `Python` | Where `host.py` lives |
| `Python:Venv` | `Python/.venv` | This app's environment |
| `Python:RuntimeDll` | auto | Overrides DLL discovery. Normally unnecessary. |
| `Python:StartupTimeoutSeconds` | 180 | Raise it if first start is slow. |

`Python/.env` mirrors `OpsLookup.Graph/.env`. `host.py` loads it from its own
directory, because `load_dotenv()` otherwise resolves against the process working
directory, which is the project root under ASP.NET Core — and startup then fails
on a missing `API_TOKEN`.

Dev tokens from `/auth/dev-token` last **60 minutes**. When it expires, lookups
return 401 and the agent reports that in plain language; refresh the token in
`Python/.env`.

## Troubleshooting

Read `Python/logs/progress.log` first. It is written by plain file I/O, so it
works even when the host is wedged and nothing else is logging.

| Symptom | Cause |
|---|---|
| `pythonReady: false` forever, `progress.log` empty | `host.py` never imported. Check `Python/.venv` exists. |
| `API_TOKEN is required` | `.env` not found by `host.py`, or the key is blank |
| 401s in the data API log | `API_TOKEN` expired — re-mint (60 min) |
| `Looked up 0 times` on every answer | The model cannot emit tool calls. **Every answer is then a guess.** Check `LONGCAT_BASE_URL`. |
| `Event loop is closed` | Usually a stale `.venv` or a `host.py` edited back to `asyncio.run` |
| `Failed to import encodings` | `PythonPath` lost the base install's `Lib` |

For a raw view of the interpreter state:

```powershell
Invoke-RestMethod http://localhost:5100/api/health | ConvertTo-Json -Depth 5
```

## Development

```powershell
# page with hot reload (proxies /api to :5100)
cd ClientApp ; npm run dev        # http://localhost:5173

# rebuild the page after changing ClientApp/src
npm run build

# Python only — test a turn without ASP.NET Core
cd ..\Python ; .\.venv\Scripts\python.exe -c "import host; print(host.run_turn('T-1','contracts for social ID 1234567890'))"
```