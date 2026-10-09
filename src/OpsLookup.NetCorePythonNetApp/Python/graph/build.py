"""The graph: start -> llm -> (action -> llm)* -> end.

Two exits out of `llm`:
  - tool calls present -> route to `action`
  - plain text only     -> route to `end`, and that text is the reply

A clarifying question is therefore the same code path as a final answer: both are
plain text with no tool call.
"""

import logging
from typing import Annotated, TypedDict

from langchain_core.messages import AnyMessage, SystemMessage
from langchain_core.tools import StructuredTool
from langgraph.graph import END, START, MessagesState, StateGraph
from langgraph.prebuilt import ToolNode

logger = logging.getLogger(__name__)

SYSTEM_PROMPT = """You are a support assistant for IT operations staff.

They are not technical. Describe what you found in plain language, not jargon,
and keep answers short.

How to work:
- Use the get_contract tool whenever a member asks about a party's contracts.
- A social ID is {rule}. If a member has not given you one, or gave you
  something that does not match that rule, ask them for it plainly instead of
  calling the tool. Never guess or invent a social ID.
- The tool returns an empty result when there are no matching contracts. Say so
  directly; do not pad the answer with speculation.
- If the tool returns an error, report what went wrong rather than retrying
  repeatedly.
- Report only what the tool returned. Do not add details it did not give you.
""".format(rule="10 digits starting with 1 or 2")


class TicketState(MessagesState):
    """Conversation state for one ticket thread."""

    ticket_id: str
    operator_id: str


def route_after_llm(state: TicketState) -> str:
    """Conditional edge: tool calls go to action, anything else finishes the turn."""
    last: AnyMessage = state["messages"][-1]
    return "action" if getattr(last, "tool_calls", None) else END


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