"""Functional checks for the Semantic Kernel agent.

Mirrors what the Python smoke test asserts about behaviour, so the two agents
can be compared rather than trusted.
"""

import json
import subprocess
import sys
import time
import urllib.request

BASE = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:5101"

passed = 0
failed = 0


def check(label, condition):
    global passed, failed
    if condition:
        passed += 1
        print(f"  [PASS] {label}")
    else:
        failed += 1
        print(f"  [FAIL] {label}")


def chat(ticket, message, locale="en", timeout=180):
    body = json.dumps(
        {"ticketId": ticket, "message": message, "locale": locale}
    ).encode("utf-8")
    req = urllib.request.Request(
        f"{BASE}/api/chat", data=body, headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def health():
    with urllib.request.urlopen(f"{BASE}/api/health", timeout=30) as r:
        return json.load(r)


ARABIC = (
    "\u0645\u0646 \u0647\u0648 \u0645\u062d\u0627\u0645\u064a \u0627\u0644\u0631\u0642\u0645 "
    "\u0627\u0644\u0648\u0637\u0646\u064a \u0627\u0644\u0645\u0648\u062d\u062f 2111111111"
    "\u060c \u0648\u0647\u0644 \u0647\u0648 \u0645\u0639\u062a\u0645\u062f\u061f"
)
MOHAMI = "\u0627\u0644\u0645\u062d\u0627\u0645\u064a"

print("1. health")
h = health()
check("reports ready", h.get("ready") is True)
check("identifies as semantic-kernel", h.get("agent") == "semantic-kernel")
check("data API reachable", h.get("apiReachable") is True)

print("2. contracts by national unified ID")
r = chat("T-contracts", "What contracts are held for national unified ID 1234567890?")
check("used a tool", r["toolCalls"] >= 1)
check("lookup succeeded", r["lookupFailed"] is False)
check("no error", r["error"] is False)
check("named the first contract", "Annual Maintenance Agreement" in r["reply"])
check("named the second contract", "Network Support Retainer" in r["reply"])
check("reported the contract code", "100234" in r["reply"])

print("3. attorney lookup, approved")
r = chat("T-attorney", "Who is the agent for national unified ID 1987654321, and are they approved?")
check("used a tool", r["toolCalls"] >= 1)
check("lookup succeeded", r["lookupFailed"] is False)
check("named the attorney", "Lena Fischer" in r["reply"])
check("reported the status verbatim", "approved" in r["reply"])

print("4. attorney lookup, flagged")
r = chat("T-flagged", "Is the agency for national unified ID 2111111111 approved?")
check("lookup succeeded", r["lookupFailed"] is False)
check("reported faked", "faked" in r["reply"])
check("did not soften it", "approved to act" not in r["reply"])

print("5. Arabic")
r = chat("T-arabic", ARABIC, locale="ar")
check("used a tool", r["toolCalls"] >= 1)
check("lookup succeeded", r["lookupFailed"] is False)
check("replied in Arabic script", any("\u0600" <= c <= "\u06ff" for c in r["reply"]))
check("named the attorney", "Marcus Webb" in r["reply"])

print("6. multi-turn context")
first = chat("T-thread", "What contracts are held for national unified ID 1234567890?")
second = chat("T-thread", "And who is the agent for that one?")
check("first turn looked up", first["toolCalls"] >= 1)
check("second turn looked up", second["toolCalls"] >= 1)
check("second turn succeeded", second["lookupFailed"] is False)
check("answered about the agency", "Lena" in second["reply"] or "agent" in second["reply"].lower())

print("7. invalid national unified ID")
r = chat("T-invalid", "What contracts are held for national unified ID 999?")
# lookupFailed is NOT the right assertion here. The prompt tells the model to ask
# for a well-formed identifier rather than call the tool, so the correct outcome
# is that no tool runs at all. A lookup that never happened has not failed.
check("did not attempt a lookup", r["toolCalls"] == 0)
check("no failed lookup reported", r["lookupFailed"] is False)
check("asked rather than invented", any(
    word in r["reply"].lower() for word in ("10 digits", "ten digits", "starting with")
))
check("did not return contract data", "Annual Maintenance Agreement" not in r["reply"])

print("8. missing identifier is a clarifying question, not a lookup")
r = chat("T-noid", "What contracts does this customer have?")
check("no fabricated data", "Annual Maintenance Agreement" not in r["reply"])
check("asked for the identifier", any(
    word in r["reply"].lower() for word in ("unified id", "national", "digit")
))

print("9. unknown locale falls back, does not raise")
r = chat("T-locale", "What contracts are held for national unified ID 1234567890?", locale="zz")
check("still answered", "Annual Maintenance Agreement" in r["reply"])

print("10. clearing a thread starts fresh")
chat("T-clear", "What contracts are held for national unified ID 1234567890?")
req = urllib.request.Request(f"{BASE}/api/chat/T-clear/clear", data=b"", method="POST")
with urllib.request.urlopen(req, timeout=30):
    pass
time.sleep(0.5)
r = chat("T-clear", "What contracts are held for national unified ID 1234567890?")
check("still works after clearing", "Annual Maintenance Agreement" in r["reply"])

print("11. bad request rejected")
try:
    chat("", "no ticket id")
    check("rejected empty ticketId", False)
except urllib.error.HTTPError as e:
    check("rejected empty ticketId", e.code == 400)

print()
if failed:
    print(f"FAILED: {failed} of {passed + failed}")
    sys.exit(1)
print(f"All {passed} checks passed.")