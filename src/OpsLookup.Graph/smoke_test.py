"""Smoke test for the graph wiring.

Exercises the real agent loop against the real .NET API using a stubbed model,
so the loop can be verified without a LongCat key. Checks that:

  1. A tool call routes to `action`, not `end`
  2. The tool result comes back from the API and reaches the next `llm` node
  3. A plain-text reply routes to `end`
  4. Thread state survives across separate invocations on one thread_id
  5. The attorney tool resolves by social ID and by party name, and reports status

Run:  python smoke_test.py
Needs the .NET API listening on API_BASE_URL and API_TOKEN set in .env
"""

import asyncio
import json
import os
import sys

from dotenv import load_dotenv
import httpx

from graph.build import (
    ATTORNEY_STATUSES,
    SOCIAL_ID_RULE,
    any_tool_error,
    build_graph,
    route_after_llm,
    system_prompt,
)
from langchain_core.messages import AIMessage, HumanMessage, ToolMessage
from langgraph.checkpoint.memory import InMemorySaver
from langgraph.graph import END

from tools.api_client import LookupApiClient
from tools.attorneys import build_attorney_tools
from tools.contracts import build_tools

load_dotenv()


class StubToolLlm:
    """Emits one tool call with fixed args, then a text answer once it has
    seen the result. Parameterised by tool name and args so the same stub can
    drive any tool without the model being real."""

    def __init__(self, tool_name: str, args: dict) -> None:
        self.tool_name = tool_name
        self.args = args

    async def ainvoke(self, messages):
        saw_tool_result = any(isinstance(m, ToolMessage) for m in messages)
        if not saw_tool_result:
            return AIMessage(
                content="",
                tool_calls=[
                    {
                        "name": self.tool_name,
                        "args": self.args,
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


class StubLlm(StubToolLlm):
    """Backwards-compatible contract lookup stub."""

    def __init__(self, social_id: int) -> None:
        super().__init__("get_contract", {"social_id": social_id})


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


async def mint_dev_token(base_url: str, operator_id: str) -> str:
    """Ask the Development-only endpoint for a short-lived token.

    The smoke test used to read a long-lived API_TOKEN out of .env. That stopped
    working when the agent moved to per-turn service tokens, and .env no longer
    carries one by design -- an `API_TOKEN` sitting in a file is exactly the
    credential the agent is not supposed to hold. The dev-token endpoint exists
    for this case, is gated to Development, and issues a token that expires.
    """
    url = f"{base_url}/auth/dev-token?operatorId={operator_id}&minutes=30"
    try:
        async with httpx.AsyncClient(timeout=20) as http:
            response = await http.post(url)
    except httpx.HTTPError as exc:
        print(f"Could not reach the dev-token endpoint at {url}: {exc}")
        return ""
    if response.status_code != 200:
        print(
            f"dev-token returned HTTP {response.status_code}. It only exists in "
            "Development - start the API with ASPNETCORE_ENVIRONMENT=Development."
        )
        return ""
    return response.json().get("accessToken", "")


async def main() -> int:
    base_url = os.getenv("API_BASE_URL", "http://localhost:5250").rstrip("/")
    operator_id = os.getenv("OPERATOR_ID", "smoke-test")

    token = await mint_dev_token(base_url, operator_id)
    if not token:
        # Fall back to an explicit token for anyone running against a
        # non-Development API that has a ServiceSecret configured.
        token = os.getenv("API_TOKEN", "")
        if not token:
            print(
                "No token available. Run the API with ASPNETCORE_ENVIRONMENT=Development, "
                "or set API_TOKEN in .env."
            )
            return 1

    client = LookupApiClient(base_url, token, operator_id)
    if not await client.health():
        print(f"The .NET API is not reachable at {base_url}. Start it first.")
        return 1

    tools = build_tools(client)
    attorney_tools = build_attorney_tools(client)
    all_tools = tools + attorney_tools

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

    # --- 4b. any_tool_error, the badge's input -------------------------------
    # toolCalls counts invocations, not successes. These guard the case where a
    # client would otherwise badge a failed lookup as verified.
    print("4b. any_tool_error")
    ok_tool = ToolMessage(content=json.dumps({"results": [{"contractCode": 1}]}), tool_call_id="a")
    err_tool = ToolMessage(content=json.dumps({"error": "token rejected"}), tool_call_id="b")

    check(
        "successful tool -> not a failure",
        not any_tool_error([HumanMessage("q"), ok_tool]),
    )
    check(
        "failed tool -> failure",
        any_tool_error([HumanMessage("q"), err_tool]),
    )
    check(
        "no tool calls -> not a failure",
        not any_tool_error([HumanMessage("q"), AIMessage(content="just text")]),
    )
    check(
        # The regression this exists for: the checkpointer replays the whole
        # thread, so an old failure must not taint a later good turn.
        "stale failure from an earlier turn is ignored",
        not any_tool_error([HumanMessage("old"), err_tool, HumanMessage("new"), ok_tool]),
    )
    check(
        "unparseable tool output is not asserted as a failure",
        not any_tool_error([HumanMessage("q"), ToolMessage(content="not json", tool_call_id="c")]),
    )

    # --- 4c. locale selects the system prompt --------------------------------
    # The page language has to reach the model, or members read an Arabic UI
    # beside an English answer.
    print("4c. system_prompt by locale")
    ar = system_prompt("ar")
    en = system_prompt("en")
    check("ar and en differ", ar != en)
    check("arabic prompt is arabic", any("\u0600" <= c <= "\u06ff" for c in ar))
    check("english prompt is english", "support assistant" in en.lower())
    check("unknown locale falls back, not raises", system_prompt("fr") == ar)
    check("none falls back", system_prompt(None) == ar)
    check("empty falls back", system_prompt("") == ar)
    check("case insensitive", system_prompt("AR") == ar)
    for name, prompt in (("ar", ar), ("en", en)):
        check(f"{name}: placeholders substituted", "{" not in prompt and "}" not in prompt)
        check(f"{name}: social ID rule injected", SOCIAL_ID_RULE in prompt)
        check(f"{name}: attorney statuses injected", ATTORNEY_STATUSES in prompt)

    # --- 5. attorney tool ----------------------------------------------------
    print("5. attorney by social ID")

    async def run_attorney_tool(tool_name: str, args: dict, thread: str) -> str:
        graph_a = build_graph(
            StubToolLlm(tool_name, args), all_tools, InMemorySaver()
        )
        out = await graph_a.ainvoke(
            {"messages": [("user", "look up the attorney")]},
            {"configurable": {"thread_id": thread}, "recursion_limit": 10},
        )
        return tool_content(out)

    check("both tools are registered", [t.name for t in all_tools] == ["get_contract", "get_attorney"])

    # B Karimi (1987654321) has an approved attorney; C Delacroix (2111111111)
    # has one flagged as faked. Both come from DevSeeder.
    approved = await run_attorney_tool("get_attorney", {"social_id": 1987654321}, "smoke-5a")
    print(f"     approved: {approved[:200]}")
    check("attorney returned a result", '"results"' in approved)
    check("status came through", "approved" in approved)
    check("party came with it", "Karimi" in approved)
    check("not an error", '"error"' not in approved)

    faked = await run_attorney_tool("get_attorney", {"social_id": 2111111111}, "smoke-5b")
    print(f"     faked: {faked[:200]}")
    check("flagged status is reported as-is", "faked" in faked)

    print("6. attorney by party name")
    by_name = await run_attorney_tool("get_attorney", {"party_name": "karimi"}, "smoke-6")
    print(f"     by name: {by_name[:200]}")
    check("partial name matched", "Lena Fischer" in by_name)

    print("7. attorney with no attorney on file")
    # A Nasser (1234567890) is deliberately seeded with no attorney.
    none_found = await run_attorney_tool("get_attorney", {"social_id": 1234567890}, "smoke-7")
    print(f"     no attorney: {none_found[:200]}")
    check("empty result list, not an error", '"results": []' in none_found.replace('"results":[]', '"results": []'))

    print("8. attorney tool rejects a bad social ID")
    bad_id = await run_attorney_tool("get_attorney", {"social_id": 3456789012}, "smoke-8")
    print(f"     bad id: {bad_id[:200]}")
    check("reported as an error the model can explain", '"error"' in bad_id)

    await client.aclose()
    print("\nAll smoke tests passed.")
    return 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))