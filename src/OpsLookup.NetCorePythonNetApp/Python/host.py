"""Bridge between the ASP.NET Core host and the LangGraph agent.

Design note
-----------
An earlier version ran the graph on a Python background thread with a
persistent asyncio loop. Embedded in .NET that thread never actually executed,
and because the .NET side had to poll it through ``Py.GIL()`` the failure showed
up only as a silent startup hang. Both problems come from the same root: crossing
a thread boundary to reach Python.

So each turn runs synchronously on the thread that called in, which already holds
the GIL. The coroutine runs on that same thread, on a loop created once per process
by ``_get_loop()``, so there is no hand-off at all. Module imports are cached in
``sys.modules``, so only the first turn pays the ~12s import cost.

Concurrency is still fine: while a turn is waiting on the model or on SQL Server,
socket I/O releases the GIL, so another request's thread can run its own turn at
the same time. Each turn builds its own graph and checkpointer, so nothing is
shared. In practice turns are effectively serialised by that GIL, which is fine
for an internal tool.

What .NET gets is one synchronous entry point:

    result_json = host.run_turn(ticket_id, message, operator_id)
"""

import asyncio
import json
import logging
import os
import sys
import threading
import time
import traceback
from contextlib import AsyncExitStack
from pathlib import Path
from typing import Any

PYTHON_DIR = Path(__file__).resolve().parent

# Where this process may write: logs/ and the SQLite checkpointer.
#
# Defaults to the app's own Python directory, which is fine when the app owns its
# folder. It usually is not under IIS: the application directory is effectively
# read-only in practice, the app pool identity has no write access to it, and both
# the log file and the checkpoint database would silently fail to be created.
# Point this at something writable, e.g. C:\ProgramData\AI.TicketingAssistant.
#
# Every use of this is best-effort by design: a diagnostics path that raises would
# take the application down with it.
DATA_DIR = Path(os.environ.get("OPSLOOKUP_DATA_DIR") or PYTHON_DIR)


def _log_dir() -> Path:
    return DATA_DIR / "logs"


def _configure_logging() -> None:
    """Send every Python log record to a file, and never to stderr.

    langchain and langgraph build their own ``getLogger(__name__)`` loggers. With
    no handler on the root logger, Python's last-resort handler writes anything at
    WARNING or above to ``sys.stderr``. Under IIS the process has no console, and
    a blocked stderr write happens *while the GIL is held* -- which stalls every
    ``Py.GIL()`` call in .NET. The symptom is requests that hang with no error and
    no log line, which is the worst kind of failure to diagnose.

    Adding any root handler suppresses that fallback, so this has to run before
    the first turn even if it cannot open the file.
    """
    root = logging.getLogger()
    level = os.getenv("LOG_LEVEL", "INFO").upper()

    try:
        directory = _log_dir()
        directory.mkdir(parents=True, exist_ok=True)
        handler: logging.Handler = logging.FileHandler(
            directory / "python.log", encoding="utf-8"
        )
    except Exception:  # noqa: BLE001 - must never break startup
        # Unwritable directory. A null handler still stops anything reaching
        # stderr, which is the point; we simply lose the detail.
        handler = logging.NullHandler()

    handler.setFormatter(
        logging.Formatter("%(asctime)s %(levelname)-7s %(name)s: %(message)s")
    )
    root.handlers[:] = [handler]
    root.setLevel(level)

# Load .env from this file's own directory. config.py also calls load_dotenv(),
# but that resolves against the process working directory -- which is the project
# root when hosted by ASP.NET Core, not Python/. So without this the settings come
# back empty and startup fails on a missing API_TOKEN.
from dotenv import load_dotenv  # noqa: E402

load_dotenv(PYTHON_DIR / ".env", override=False)

# Before the library imports below, so their loggers are captured by a handler
# from the moment they exist. See _configure_logging for why this cannot be left
# to chance.
_configure_logging()

# Imported at module scope so the cost is paid once, at warmup, rather than on
# the first ticket. `host` is imported after sys.path is prepared by .NET.
from config import load_settings  # noqa: E402
from graph.build import any_tool_error, build_graph  # noqa: E402
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver  # noqa: E402
from llm import build_llm  # noqa: E402
from tools.api_client import LookupApiClient  # noqa: E402
from tools.attorneys import build_attorney_tools  # noqa: E402
from tools.contracts import build_tools  # noqa: E402

_warm = threading.Event()

# One event loop for the process, created lazily on whichever thread calls in
# (always the .NET python-worker thread). asyncio.run() per turn would hand each
# turn a fresh loop while httpx connection pools outlive it, and reusing a pool
# against a closed loop fails with "Event loop is closed" mid-llm-call.
_loop: asyncio.AbstractEventLoop | None = None


def _get_loop() -> asyncio.AbstractEventLoop:
    global _loop
    if _loop is None or _loop.is_closed():
        _loop = asyncio.new_event_loop()
        asyncio.set_event_loop(_loop)
    return _loop


def _log(message: str) -> None:
    """Append to logs/progress.log.

    Embedded sys.stderr is not reliably attached to the host console, and a
    blocked write on it happens with the GIL held, which stalls every Py.GIL()
    call in .NET. A file write involves neither, so it is safe and readable from
    outside while the host is busy.
    """
    try:
        log_dir = _log_dir()
        log_dir.mkdir(parents=True, exist_ok=True)
        with open(log_dir / "progress.log", "a", encoding="utf-8") as handle:
            handle.write(f"{time.strftime('%H:%M:%S')} {message}\n")
    except Exception:  # noqa: BLE001 - diagnostics must never raise
        pass


def _describe(exc: Exception) -> str:
    text = str(exc)
    if "MissingSessionID" in text or "x-opencode-session" in text:
        return "The model endpoint rejected the request."
    if "401" in text or "invalid_api_key" in text:
        return "The model rejected the API key."
    if "404" in text or "model_not_found" in text:
        return "The configured model was not found."
    if "timeout" in text.lower():
        return "The model did not respond in time."
    return "The agent could not complete this request."


# --------------------------------------------------------------------------- warmup
def warmup() -> str:
    """Import everything and verify configuration. Blocking, call at startup.

    Returns an empty string on success, otherwise the reason. The GIL must be
    held by the caller.
    """
    if _warm.is_set():
        return ""

    _log("warmup: begin")
    try:
        # require_api_token=False: the .NET host mints a token per turn and
        # passes it in, so .env no longer carries one. The strict default would
        # raise here and take the entire warmup down with a message about a
        # variable this process is never supposed to read.
        settings = load_settings(require_api_token=False)
        _log(f"warmup: settings ok api={settings.api_base_url} model={settings.longcat_model}")

        checkpoint = _checkpoint_path(settings)
        client = LookupApiClient(
            base_url=settings.api_base_url,
            # Not used for lookups: warmup only proves these objects construct.
            # Per-turn lookups get a real token from the host via _run_turn.
            token=settings.api_token,
            operator_id=settings.operator_id,
            timeout_seconds=settings.request_timeout_seconds,
        )

        tools = build_tools(client) + build_attorney_tools(client)
        build_llm(settings, tools)
        _log(f"warmup: graph components built ({len(tools)} tools)")
    except Exception as exc:  # noqa: BLE001 - reported to .NET as a string
        _log(f"warmup: FAILED {type(exc).__name__}: {exc}")
        _log(traceback.format_exc())
        return f"{type(exc).__name__}: {exc}"

    _warm.set()
    _log("warmup: ready")
    return ""


def _checkpoint_path(settings: Any) -> str:
    """Absolute checkpoint path, pinned to this app's writable data directory.

    A relative value would otherwise resolve against the process working
    directory, so two apps started from different folders could collide on one
    database file -- and under IIS the working directory is not the app folder.
    Set CHECKPOINT_PATH to an absolute path to override the base.
    """
    path = Path(settings.checkpoint_path)
    if not path.is_absolute():
        path = DATA_DIR / path
    return str(path)


# ---------------------------------------------------------------------- one turn
async def _run_turn(
    thread_id: str,
    message: str,
    operator_id: str,
    locale: str = "",
    api_base_url: str = "",
    api_token: str = "",
) -> dict:
    async with AsyncExitStack() as stack:
        settings = load_settings(require_api_token=not api_token)

        client = LookupApiClient(
            # Passed in by the .NET host, which owns credential acquisition. The
            # .env value is only the fallback for the standalone api.py, so that
            # agent code has no way to obtain a token for itself.
            base_url=api_base_url or settings.api_base_url,
            token=api_token or settings.api_token,
            operator_id=operator_id or settings.operator_id,
            timeout_seconds=settings.request_timeout_seconds,
        )
        stack.push_async_callback(client.aclose)

        # A fresh graph and checkpointer per turn, so nothing is shared between
        # concurrent turns. The event loop itself is shared, deliberately: httpx
        # connection pools are bound to it and break against a closed one.
        checkpointer = await stack.enter_async_context(
            AsyncSqliteSaver.from_conn_string(_checkpoint_path(settings))
        )

        tools = build_tools(client) + build_attorney_tools(client)
        graph = build_graph(build_llm(settings, tools), tools, checkpointer)

        result = await graph.ainvoke(
            {
                "messages": [("user", message)],
                "ticket_id": thread_id,
                "operator_id": operator_id or settings.operator_id,
                # Selects the system prompt, so the reply comes back in the same
                # language the page is showing.
                "locale": locale,
            },
            {
                "configurable": {"thread_id": thread_id},
                "recursion_limit": settings.recursion_limit,
            },
        )

        messages = result["messages"]
        reply = messages[-1].content
        tool_calls = sum(len(getattr(m, "tool_calls", []) or []) for m in messages)

        return {
            "threadId": thread_id,
            "reply": reply if isinstance(reply, str) else str(reply),
            "toolCalls": tool_calls,
            "lookupFailed": any_tool_error(messages),
        }


def run_turn(
    thread_id: str,
    message: str,
    operator_id: str = "",
    locale: str = "",
    api_base_url: str = "",
    api_token: str = "",
) -> str:
    """Run one turn and return the outcome as a JSON string.

    Blocking. The GIL must be held by the caller, and it is held for the whole
    call -- that is safe because socket waits release it, which is what allows
    a second request to make progress at the same time.

    `locale` picks the system prompt, so the answer comes back in the language
    the page is showing. `api_base_url` and `api_token` come from the .NET host,
    which is the only component that can obtain a token; when they are absent
    the values from `.env` are used, which is what the standalone api.py does.
    """
    failure = warmup()
    if failure:
        return json.dumps({"error": failure})

    _log(f"turn begin: thread={thread_id} locale={locale or '(default)'} msg={message[:60]!r}")
    try:
        payload = _get_loop().run_until_complete(
            _run_turn(thread_id, message, operator_id, locale, api_base_url, api_token)
        )
    except Exception as exc:  # noqa: BLE001 - reported back to the caller
        _log(f"turn failed: {type(exc).__name__}: {exc}")
        _log(traceback.format_exc())
        return json.dumps({"error": _describe(exc), "errorType": type(exc).__name__})

    _log(f"turn done: toolCalls={payload.get('toolCalls')}")
    return json.dumps(payload, default=str)


async def _api_reachable_async() -> bool:
    # Same reason as warmup: this process does not hold a token, so the strict
    # default would raise and /api/health would report apiReachable=false for
    # the life of the process. /health on the data API is anonymous anyway.
    settings = load_settings(require_api_token=False)
    client = LookupApiClient(
        base_url=settings.api_base_url,
        token=settings.api_token,
        operator_id=settings.operator_id,
        timeout_seconds=10,
    )
    try:
        return await client.health()
    finally:
        # Must close on the same loop the client was used on. A second
        # asyncio.run here would tear down an already-closed loop and hang the
        # worker thread, which stalls every later request queued behind it.
        await client.aclose()


def api_reachable() -> str:
    """True when the .NET data API answers. Blocking; GIL must be held."""
    try:
        return "true" if _get_loop().run_until_complete(_api_reachable_async()) else "false"
    except Exception:  # noqa: BLE001 - health must never raise
        return "false"


def diagnostics() -> str:
    """Current state and thread list, for troubleshooting."""
    import io

    buffer = io.StringIO()
    try:
        import faulthandler

        faulthandler.dump_traceback(file=buffer)
    except Exception as exc:  # noqa: BLE001
        buffer.write(f"faulthandler unavailable: {exc}\n")

    return json.dumps(
        {
            "warm": _warm.is_set(),
            "python": sys.version,
            "pid": os.getpid(),
            "threads": [t.name for t in threading.enumerate()],
            "trace": buffer.getvalue(),
        },
        indent=2,
        default=str,
    )