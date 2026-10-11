"""The graph: start -> llm -> (action -> llm)* -> end.

Two exits out of `llm`:
  - tool calls present -> route to `action`
  - plain text only     -> route to `end`, and that text is the reply

A clarifying question is therefore the same code path as a final answer: both are
plain text with no tool call.
"""

import json
import logging
from typing import Annotated, TypedDict

from langchain_core.messages import AnyMessage, HumanMessage, SystemMessage, ToolMessage
from langchain_core.tools import StructuredTool
from langgraph.graph import END, START, MessagesState, StateGraph
from langgraph.prebuilt import ToolNode

logger = logging.getLogger(__name__)

SYSTEM_PROMPT = """You are a support assistant for IT operations staff.

They are not technical. Describe what you found in plain language, not jargon,
and keep answers short.

How to work:
- Use get_contract when a member asks about a party's contracts.
- Use get_attorney when a member asks who represents a party, or whether an
  attorney is approved. It searches by social ID, party name, or both.
- A social ID is {rule}. If a member has not given you one, or gave you
  something that does not match that rule, ask them for it plainly instead of
  calling the tool. Never guess or invent a social ID.
- An attorney status is {statuses}. Report the status exactly as the tool
  returned it, and say plainly what it means: "approved" means cleared to act,
  "faked" means flagged as fraudulent. Do not soften or reword the status.
- An empty result means no attorney is on file for that party. Say that directly.
- If a tool returns an error, report what went wrong rather than retrying
  repeatedly.
- Report only what the tools returned. Do not add details they did not give you.
""".format(rule="10 digits starting with 1 or 2", statuses="approved or faked")


class TicketState(MessagesState):
    """Conversation state for one ticket thread."""

    ticket_id: str
    operator_id: str


def route_after_llm(state: TicketState) -> str:
    """Conditional edge: tool calls go to action, anything else finishes the turn."""
    last: AnyMessage = state["messages"][-1]
    return "action" if getattr(last, "tool_calls", None) else END


def any_tool_error(messages: list[AnyMessage]) -> bool:
    """True if a tool run *in this turn* reported a failure.

    A tool-call count alone cannot answer this. `toolCalls` counts invocations,
    not successes, so a turn where the API rejected the token still counts 1 —
    and a client reading that as "verified against the database" is being told
    something false. Both tools return failures as `{"error": ...}` JSON text
    rather than raising, so the signal has to be read back out of the message.

    Scoped to the current turn deliberately. The checkpointer replays the whole
    thread into `messages`, so a failure from an earlier turn is still in that
    list; scanning all of it marks this turn's good answer as failed because of
    something that happened earlier. The last HumanMessage is the one just sent,
    so everything after it belongs to this turn.
    """
    start = 0
    for index in range(len(messages) - 1, -1, -1):
        if isinstance(messages[index], HumanMessage):
            start = index
            break

    for message in messages[start:]:
        if not isinstance(message, ToolMessage):
            continue
        content = message.content
        if not isinstance(content, str) or not content.startswith("{"):
            continue
        try:
            if "error" in json.loads(content):
                return True
        except (ValueError, TypeError):
            # Not JSON, or not an object. Treat as "cannot tell" rather than
            # asserting a failure we cannot evidence.
            continue
    return False


def build_graph(
    llm,
    tools: list[StructuredTool],
    checkpointer,
) -> object:
    """Compile the agent loop.

    `llm` must already have its tools bound, so a turn can emit tool calls.
    """

    async def llm_node(state: TicketState) -> dict:
        messages = [SystemMessage(content=SYSTEM_PROMPT), *state["messages"]]
        reply = await llm.ainvoke(messages)
        return {"messages": [reply]}

    builder = StateGraph(TicketState)
    builder.add_node("llm", llm_node)
    builder.add_node("action", ToolNode(tools))

    builder.add_edge(START, "llm")
    builder.add_conditional_edges(
        "llm",
        route_after_llm,
        {"action": "action", END: END},
    )
    builder.add_edge("action", "llm")

    return builder.compile(checkpointer=checkpointer)