"""Attorney lookup tool.

An operations member asking "is my attorney approved?" needs two things
answered at once: who the attorney is, and what their standing is. The result
carries the matched party as well, so the member can see that the attorney found
is the one attached to the party they asked about.
"""

import json
import logging

from langchain_core.tools import StructuredTool
from pydantic import BaseModel, Field

from tools.api_client import LookupApiClient, LookupApiError

logger = logging.getLogger(__name__)

SOCIAL_ID_RULE = "10 digits, starting with 1 or 2"

STATUSES = "approved or faked"


class GetAttorneyInput(BaseModel):
    social_id: int | None = Field(
        default=None,
        description=(
            "The party's social ID. Must be exactly "
            f"{SOCIAL_ID_RULE}. Omit if you do not have it."
        ),
    )
    party_name: str | None = Field(
        default=None,
        description=(
            "Partial or full party name, for example 'Karimi'. "
            "Omit if you do not have one."
        ),
    )


def build_attorney_tools(client: LookupApiClient) -> list[StructuredTool]:
    async def get_attorney(
        social_id: int | None = None,
        party_name: str | None = None,
    ) -> str:
        """Look up the attorney acting for a party, and that attorney's standing.

        Search by social ID, party name, or both. At least one is required.
        Returns a JSON array; each item has attorneyId, attorneyName, status,
        partyName and socialId. An empty array means no attorney is on file for
        that party.
        """
        if social_id is None and not party_name:
            return json.dumps(
                {
                    "error": "Provide a social ID, a party name, or both."
                }
            )

        try:
            rows = await client.get_attorneys(
                social_id=social_id, party_name=party_name
            )
        except LookupApiError as exc:
            # Returned as text so the model explains the failure to the member
            # rather than the run blowing up.
            return json.dumps({"error": str(exc)})

        return json.dumps({"results": rows})

    return [
        StructuredTool(
            name="get_attorney",
            description=(
                "Look up the attorney acting for a party, and whether that "
                f"attorney's status is {STATUSES}. Search by the party's social ID "
                "(must be " + SOCIAL_ID_RULE + ") or by party name, or both. "
                "Use this whenever a member asks who represents a party, or "
                "whether an attorney is approved or flagged."
            ),
            args_schema=GetAttorneyInput,
            # `coroutine` is set explicitly. from_function() does not reliably
            # infer an async closure, and silently wraps it as sync -- which runs
            # the coroutine without ever awaiting it.
            coroutine=get_attorney,
        )
    ]