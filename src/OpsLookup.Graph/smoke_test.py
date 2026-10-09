"""Smoke test for the graph wiring.

Exercises the real agent loop against the real .NET API using a stubbed model,
so the loop can be verified without a LongCat key. Checks that:

  1. A tool call routes to `action`, not `end`
  2. The tool result comes back from the API and reaches the next `llm` node
  3. A plain-text reply routes to `end`
  4. Thread state survives across separate invocations on one thread_id

Run:  python smoke_test.py
Needs the .NET API listening on API_BASE_URL and API_TOKEN set in .env
"""

import asyncio
import os
import sys

from dotenv import load_dotenv

from graph.build import build_graph, route_after_llm
from langchain_core.messages import AIMessage, HumanMessage, ToolMessage
from langgraph.checkpoint.memory import InMemorySaver
from langgraph.graph import END

from tools.api_client import LookupApiClient
from tools.contracts import build_tools

load_dotenv()


class StubLlm:
    """Emits one tool call, then a text answer once it has seen the result."""

    def __init__(self, social_id: int) -> None:
        self.social_id = social_id

    async def ainvoke(self, messages):
        saw_tool_result = any(isinstance(m, ToolMessage) for m in messages)
        if not saw_tool_result:
            return AIMessage(
                content="",
                tool_calls=[
                    {
                        "name": "get_contract",
                        "args": {"social_id": self.social_id},
                        "id": "call_1",
                    }
                ],
            )
        results = next(
            m.content
            for m in messages
            if isinstance(m, ToolMessage) and m.tool_call_id == "call_1"
        )
        return AIMessage(content=f"Tool returned {len(results)} chars")


class StubAskingLlm:
    """Emits no tool call, so the graph must go straight to `end`."""

    async def ainvoke(self, messages):
        return AIMessage(content="Which social ID would you like me to look up?")


def tool_content(result) -> str:
    """Content of the first ToolMessage in a graph result."""
    return next(
        m.content
        for m in result["messages"]
        if isinstance(m, ToolMessage)
    )


def check(label: str, condition: bool) -> None:
    status = "PASS" if condition else "FAIL"
    print(f"  [{status}] {label}")
    if not condition:
        raise AssertionError(label)


async def main() -> int:
    base_url = os.getenv("API_BASE_URL", "http://localhost:5250").rstrip("/")
    token = os.getenv("API_TOKEN", "")
    if not token:
        print("API_TOKEN is not set. See .env.example.")
        return 1

    client = LookupApiClient(base_url, token, os.getenv("OPERATOR_ID", "smoke-test"))
    if not await client.health():
        print(f"The .NET API is not reachable at {base_url}. Start it first.")
        return 1

    tools = build_tools(client)

    # --- 1. full loop: tool call -> action -> llm -> end --------------------
    print("1. tool-calling turn")
    graph = build_graph(StubLlm(1234567890), tools, InMemorySaver())
    result = await graph.ainvoke(
        {"messages": [("user", "check social ID 1234567890")]},
        {"configurable": {"thread_id": "smoke-1"}, "recursion_limit": 10},
    )
    kinds = [type(m).__name__ for m in result["messages"]]
    print(f"     message kinds: {kinds}")
    tool_payload = tool_content(result)
    print(f"     tool returned: {tool_payload[:160]}")
    check("emitted a ToolMessage (action node ran)", "ToolMessage" in kinds)
    check("no further tool call (llm answered)", not result["messages"][-1].tool_calls)
    check("tool result carried the contract rows", "Annual Maintenance Agreement" in tool_payload)
    check("tool result is not an error", '"error"' not in tool_payload)

    # --- 2. no tool call routes straight to end ------------------------------
    print("2. clarifying-question turn")
    graph2 = build_graph(StubAskingLlm(), tools, InMemorySaver())
    result2 = await graph2.ainvoke(
        {"messages": [("user", "check something")]},
        {"configurable": {"thread_id": "smoke-2"}, "recursion_limit": 10},
    )
    check("one message in, one reply out", len(result2["messages"]) == 2)
    check("no tool was called", not any(isinstance(m, ToolMessage) for m in result2["messages"]))

    # --- 3. thread state persists across invocations -------------------------
    print("3. thread continuity")
    config3 = {"configurable": {"thread_id": "smoke-3"}, "recursion_limit": 10}
    await graph.ainvoke({"messages": [("user", "first message")]}, config3)
    result3 = await graph.ainvoke({"messages": [("user", "second message")]}, config3)
    human = [m.content for m in result3["messages"] if isinstance(m, HumanMessage)]
    print(f"     human messages on thread: {human}")
    check("both turns' messages retained", len(human) == 2)
    check("earlier turn still in history", human[0] == "first message")
    check(
        "tool result from turn 1 still present",
        any(isinstance(m, ToolMessage) for m in result3["messages"]),
    )

    # --- 4. routing helper in isolation --------------------------------------
    print("4. route_after_llm")
    calling = AIMessage(content="", tool_calls=[{"name": "x", "args": {}, "id": "1"}])
    check("tool_calls -> action", route_after_llm({"messages": [calling]}) == "action")
    check("plain text -> end", route_after_llm({"messages": [AIMessage(content="hi")]}) == END)

    await client.aclose()
    print("\nAll smoke tests passed.")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))