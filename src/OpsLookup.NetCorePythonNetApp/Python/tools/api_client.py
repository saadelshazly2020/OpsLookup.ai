"""HTTP client for the .NET OpsLookup API.

This is the only place in the Python service that talks to the backend. There is
no database driver anywhere in this project by design: SQL Server is reachable
exclusively through the .NET API.
"""

import logging
from typing import Any

import httpx

logger = logging.getLogger(__name__)


class LookupApiError(RuntimeError):
    """Raised when the API rejects a request or is unreachable."""


class LookupApiClient:
    def __init__(
        self,
        base_url: str,
        token: str,
        operator_id: str,
        timeout_seconds: float = 20.0,
    ) -> None:
        self._base_url = base_url.rstrip("/")
        self._operator_id = operator_id
        self._client = httpx.AsyncClient(
            base_url=self._base_url,
            timeout=timeout_seconds,
            headers={
                "Authorization": f"Bearer {token}",
                "X-Operator-Id": operator_id,
            },
        )

    async def aclose(self) -> None:
        await self._client.aclose()

    async def get_contracts(self, social_id: int, name: str | None = None) -> list[dict[str, Any]]:
        """GET /api/contracts?socialId=&name= and return the rows."""
        params: dict[str, str] = {"socialId": str(social_id)}
        if name:
            params["name"] = name

        try:
            response = await self._client.get("/api/contracts", params=params)
        except httpx.HTTPError as exc:
            raise LookupApiError(f"Could not reach the lookup API: {exc}") from exc

        if response.status_code == httpx.codes.UNAUTHORIZED:
            raise LookupApiError(
                "The lookup API rejected the service token. "
                "Check API_TOKEN matches the API's Jwt:SigningKey."
            )

        if response.status_code == httpx.codes.BAD_REQUEST:
            detail = _problem_detail(response)
            raise LookupApiError(detail)

        if response.is_error:
            raise LookupApiError(
                f"The lookup API returned HTTP {response.status_code}."
            )

        rows: list[dict[str, Any]] = response.json()
        logger.info(
            "lookup social_id=%s name=%r -> %d row(s)",
            social_id,
            name,
            len(rows),
        )
        return rows

    async def health(self) -> bool:
        try:
            response = await self._client.get("/health")
            return response.status_code == httpx.codes.OK
        except httpx.HTTPError:
            return False


def _problem_detail(response: httpx.Response) -> str:
    try:
        return str(response.json().get("detail", response.text))
    except ValueError:
        return response.text