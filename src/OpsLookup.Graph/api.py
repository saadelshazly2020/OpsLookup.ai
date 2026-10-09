"""FastAPI surface in front of the LangGraph agent.

A ticket is one thread. The member's messages and the agent's replies share a
thread_id, so the checkpointer restores context when the agent needs to ask a
follow-up question.
"""

import logging
import os
from contextlib import asynccontextmanager
from pathlib import Path

from dotenv import load_dotenv
from fastapi import FastAPI, Request
from fastapi.responses import FileResponse, JSONResponse
from fastapi.staticfiles import StaticFiles
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from pydantic import BaseModel, Field

from config import Settings, load_settings
from graph.build import build_graph
from llm import build_llm
from tools.api_client import LookupApiClient
from tools.contracts import build_tools

STATIC_DIR = Path(__file__).parent / "static"

logging.basicConfig(
    level=os.getenv("LOG_LEVEL", "INFO"),
    format="%(asctime)s %(levelname)-8s %(name)s: %(message)s",
)
logger = logging.getLogger(__name__)


@asynccontextmanager
async def lifespan(app: FastAPI):
    settings = load_settings()

    client = LookupApiClient(
        base_url=settings.api_base_url,
        token=settings.api_token,
        operator_id=settings.operator_id,
        timeout_seconds=settings.request_timeout_seconds,
    )
    tools = build_tools(client)
    llm = build_llm(settings, tools)

    async with AsyncSqliteSaver.from_conn_string(settings.checkpoint_path) as checkpointer:
        app.state.settings = settings
        app.state.client = client
        app.state.graph = build_graph(llm, tools, checkpointer)
        logger.info("graph ready; api=%s model=%s", settings.api_base_url, settings.longcat_model)
        try:
            yield
        finally:
            await client.aclose()


app = FastAPI(title="OpsLookup Graph", version="0.1.0", lifespan=lifespan)

app.mount("/static", StaticFiles(directory=STATIC_DIR), name="static")


@app.get("/", include_in_schema=False)
async def index() -> FileResponse:
    """The chat page. Same origin as the API, so no CORS setup."""
    return FileResponse(STATIC_DIR / "index.html")


@app.get("/favicon.ico", include_in_schema=False)
async def favicon() -> FileResponse:
    return FileResponse(STATIC_DIR / "favicon.svg", media_type="image/svg+xml")


class ChatRequest(BaseModel):
    ticket_id: str = Field(
        description="Thread key. Reuse the same value for every message on one ticket."
    )
    message: str = Field(description="The operations member's message, in plain language.")
    operator_id: str | None = Field(default=None, description="Overrides the configured operator.")


class ChatResponse(BaseModel):
    ticket_id: str
    reply: str
    tool_calls: int


class ChatError(BaseModel):
    detail: str
    hint: str | None = None


@app.get("/health")
async def health(request: Request) -> dict:
    api_ok = await request.app.state.client.health()
    return {"status": "ok" if api_ok else "degraded", "api_reachable": api_ok}


@app.post("/chat", response_model=ChatResponse, responses={502: {"model": ChatError}})
async def chat(payload: ChatRequest, request: Request) -> ChatResponse:
    state_graph = request.app.state.graph
    settings: Settings = request.app.state.settings
    print("settings",settings)

    config = {
        "configurable": {"thread_id": payload.ticket_id},
        "recursion_limit": settings.recursion_limit,
    }

    try:
        result = await state_graph.ainvoke(
            {
                "messages": [("user", payload.message)],
                "ticket_id": payload.ticket_id,
                "operator_id": payload.operator_id or settings.operator_id,
            },
            config,
        )
    except Exception as exc:
        # A bare 500 with an empty body tells the member "the service is down" when
        # the usual cause is a misconfigured model endpoint. Say what actually failed.
        detail, hint = _describe_llm_failure(exc, settings)
        logger.error("chat failed for %s: %s", payload.ticket_id, exc, exc_info=True)
        return JSONResponse(status_code=502, content={"detail": detail, "hint": hint})

    messages = result["messages"]
    reply = messages[-1].content
    tool_calls = sum(len(getattr(m, "tool_calls", []) or []) for m in messages)

    return ChatResponse(
        ticket_id=payload.ticket_id,
        reply=reply if isinstance(reply, str) else str(reply),
        tool_calls=tool_calls,
    )


def _describe_llm_failure(exc: Exception, settings: Settings) -> tuple[str, str | None]:
    """Turn a provider exception into something an operations member can act on."""
    text = str(exc)

    if "MissingSessionID" in text or "x-opencode-session" in text:
        return (
            "The model endpoint rejected the request.",
            "The Zen gateway requires x-opencode-session and User-Agent headers. "
            "config.py adds both when the base URL points at opencode.ai/zen — "
            "check that LONGCAT_BASE_URL is spelled correctly.",
        )

    if "requires Global regions" in text or "region" in text.lower():
        return (
            "The gateway rejected this workspace's region.",
            "This is an account entitlement, not a code problem.",
        )

    if "401" in text or "invalid_api_key" in text or "Incorrect API key" in text:
        return (
            "The model rejected the API key.",
            "Check LONGCAT_API_KEY in .env.",
        )

    if "404" in text or "model_not_found" in text or "does not exist" in text:
        return (
            f"The model '{settings.longcat_model}' was not found.",
            "Check LONGCAT_MODEL, or list what is available at "
            f"{settings.longcat_base_url}/models",
        )

    if "timeout" in text.lower() or "timed out" in text.lower():
        return ("The model did not respond in time.", None)

    return ("The agent could not complete this request.", None)