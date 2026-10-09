"""LongCat chat model, reached over its OpenAI-compatible endpoint."""

import logging
from typing import Any

from langchain_core.tools import StructuredTool
from langchain_openai import ChatOpenAI

from config import Settings

logger = logging.getLogger(__name__)

# LongCat exposes its own "thinking" toggle on the OpenAI-compatible surface.
# Shape follows the model docs; verify against them before relying on it.
THINKING_OFF = {"thinking": {"type": "disabled"}}


def build_llm(
    settings: Settings,
    tools: list[StructuredTool],
    thinking: bool = False,
) -> Any:
    """Return the chat model with its tools bound.

    Thinking defaults off: this task is a parameter extraction plus a short
    plain-language answer, and operations staff are waiting on the reply.

    The OpenCode Zen gateway rejects requests that do not carry
    `x-opencode-session` and a `User-Agent`, so those are attached when the
    configured base URL points at it. A generic OpenAI client sends neither.
    """
    extra_body: dict[str, Any] = {} if thinking else THINKING_OFF
    headers = settings.default_headers()

    model = ChatOpenAI(
        model=settings.longcat_model,
        api_key=settings.longcat_api_key,
        base_url=settings.longcat_base_url,
        temperature=0,
        max_retries=2,
        timeout=90,
        extra_body=extra_body or None,
        default_headers=headers,
    )

    logger.info(
        "llm ready: model=%s base_url=%s tools=%d thinking=%s session=%s",
        settings.longcat_model,
        settings.longcat_base_url,
        len(tools),
        "on" if thinking else "off",
        settings.opencode_session_id if settings.is_opencode_go else "n/a",
    )

    return model.bind_tools(tools)