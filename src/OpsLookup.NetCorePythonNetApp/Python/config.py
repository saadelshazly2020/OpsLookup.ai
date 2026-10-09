"""Runtime configuration, read from the environment.

Kept dependency-free on purpose: no pydantic-settings, no config framework.
"""

import os
import uuid
from dataclasses import dataclass

from dotenv import load_dotenv

# Loaded here, not in the entry points. Relying on a caller to do it meant that
# importing this module from anywhere else silently fell back to defaults instead
# of raising, which reads as "my .env is being ignored".
load_dotenv()


class ConfigurationError(RuntimeError):
    pass


@dataclass(frozen=True)
class Settings:
    # .NET API
    api_base_url: str
    api_token: str
    operator_id: str

    # LongCat
    longcat_api_key: str
    longcat_base_url: str
    longcat_model: str

    # LangGraph
    checkpoint_path: str
    recursion_limit: int
    request_timeout_seconds: float

    # OpenCode Zen gateway
    opencode_session_id: str
    user_agent: str

    @property
    def is_opencode_go(self) -> bool:
        """True when talking to the Zen gateway, which needs extra headers."""
        return "opencode.ai/zen" in self.longcat_base_url

    def default_headers(self) -> dict[str, str]:
        """Headers the gateway requires that a generic OpenAI client omits.

        The Zen gateway is called from inside the OpenCode agent in normal use and
        expects those clients to identify themselves. Without them it rejects the
        request with MissingSessionID.
        """
        headers = {"User-Agent": self.user_agent}
        if self.is_opencode_go:
            headers["x-opencode-session"] = self.opencode_session_id
        return headers


def _required(name: str) -> str:
    value = os.getenv(name, "").strip()
    if not value:
        raise ConfigurationError(
            f"Environment variable {name} is required. See .env.example."
        )
    return value


def load_settings() -> Settings:
    return Settings(
        api_base_url=os.getenv("API_BASE_URL", "http://localhost:5250").rstrip("/"),
        api_token=_required("API_TOKEN"),
        operator_id=os.getenv("OPERATOR_ID", "ops-member"),
        longcat_api_key=_required("LONGCAT_API_KEY"),
        longcat_base_url=os.getenv(
            "LONGCAT_BASE_URL", "https://api.longcat.chat/openai/v1"
        ).rstrip("/"),
        longcat_model=os.getenv("LONGCAT_MODEL", "LongCat-2.5-Preview"),
        checkpoint_path=os.getenv("CHECKPOINT_PATH", "checkpoints.sqlite"),
        recursion_limit=int(os.getenv("RECURSION_LIMIT", "10")),
        request_timeout_seconds=float(os.getenv("REQUEST_TIMEOUT_SECONDS", "20")),
        opencode_session_id=os.getenv("OPENCODE_SESSION_ID") or _stable_session_id(),
        user_agent=os.getenv(
            "USER_AGENT", "OpsLookup.NetCorePythonNetApp/0.1"
        ),
    )


def _stable_session_id() -> str:
    """A session id that stays the same for the lifetime of this process.

    The gateway uses it for routing and prompt caching, so it must not change
    between requests. Set OPENCODE_SESSION_ID to pin it across restarts and keep
    the cache warm.
    """
    return f"opslookup-{uuid.uuid4().hex[:16]}"