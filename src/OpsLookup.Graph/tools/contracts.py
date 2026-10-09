"""The tools the model may call.

Exactly one data tool. It takes typed parameters and never accepts SQL, a table
name, or a free-text filter expression. The .NET API resolves the query; this
side only sends arguments.
"""

import json
import logging

from langchain_core.tools import StructuredTool
from pydantic import BaseModel, Field

from tools.api_client import LookupApiClient, LookupApiError

logger = logging.getLogger(__name__)

SOCIAL_ID_RULE = "10 digits, starting with 1 or 2"


class GetContractInput(BaseModel):
    social_id: int = Field(
        description=(
            "The party's social ID. Must be exactly "
            f"{SOCIAL_ID_RULE}. Do not guess or invent this value; "
            "ask the operations member for it if it was not provided."
        )
    )
    name: str | None = Field(
        default=None,
        description=(
            "Optional party name filter to narrow the results, "
            "for example 'Nasser'. Omit to return all contracts for the social ID."
        ),
    )


def build_tools(client: LookupApiClient) -> list[StructuredTool]:
    async def get_contract(social_id: int, name: str | None = None) -> str:
        """Look up contract records for a party, using their social ID.

        Returns a JSON array. Each item has contractCode, socialId, partyName and
        contractName. An empty array means no contracts matched, which is not the
        same as the lookup failing.
        """
        try:
            rows = await client.get_contracts(social_id=social_id, name=name)
        except LookupApiError as exc:
            # Surfaced to the model as text so it can explain the failure to the
            # operations member instead of the run blowing up.
            return json.dumps({"error": str(exc)})

        return json.dumps({"social_id": social_id, "results": rows})

    return [
        StructuredTool(
            name="get_contract",
            description=(
                "Look up contract records for a party by their social ID "
                f"(must be {SOCIAL_ID_RULE}). "
                "Use this whenever a member asks what contracts a party holds. "
                "Returns contract code, social ID, party name and contract name."
            ),
            args_schema=GetContractInput,
            # `coroutine` is set explicitly. from_function() does not reliably
            # infer an async closure, and silently wraps it as sync — which runs
            # the coroutine without ever awaiting it.
            coroutine=get_contract,
        )
    ]