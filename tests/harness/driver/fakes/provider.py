"""FakeProvider — a scripted AgentProvider for harness scenarios.

The fake returns a pre-canned ``AgentOutput`` per agent name. Each agent
maps to an ordered list of responses; agents called more than once consume
their responses in order. An agent invoked with no scripted responses (or
called more times than scripted) raises ``FakeProviderError`` with a
message that names the offending agent and points at the scenario file.

The fake never inspects ``rendered_prompt`` — its job is to drive route
coverage, not to verify prompt content. Prompt assertions belong in
lint-layer tests, not the harness.
"""

from __future__ import annotations

import asyncio
from collections.abc import Callable
from dataclasses import dataclass, field
from typing import Any

from conductor.config.schema import AgentDef
from conductor.providers.base import AgentOutput, AgentProvider


class FakeProviderError(RuntimeError):
    """Raised when the scenario doesn't script a response the workflow needs."""


@dataclass
class ScriptedResponse:
    """One scripted reply for one agent invocation."""

    content: dict[str, Any]
    raw_response: Any = None


@dataclass
class FakeProvider(AgentProvider):
    """Returns scripted ``AgentOutput`` per agent name.

    Args:
        scripts: Mapping of agent name → ordered list of responses.
    """

    scripts: dict[str, list[ScriptedResponse]] = field(default_factory=dict)
    _cursors: dict[str, int] = field(default_factory=dict, init=False, repr=False)
    _calls: list[dict[str, Any]] = field(default_factory=list, init=False, repr=False)

    @property
    def calls(self) -> list[dict[str, Any]]:
        """Audit log of every ``execute`` call, in order."""
        return self._calls

    async def execute(  # noqa: PLR0913 - signature dictated by ABC
        self,
        agent: AgentDef,
        context: dict[str, Any],
        rendered_prompt: str,
        tools: list[str] | None = None,
        interrupt_signal: asyncio.Event | None = None,
        event_callback: Callable[[str, dict[str, Any]], None] | None = None,
    ) -> AgentOutput:
        responses = self.scripts.get(agent.name)
        if not responses:
            raise FakeProviderError(
                f"FakeProvider has no scripted responses for agent {agent.name!r}; "
                f"add an entry under agent_scripts.{agent.name} in the scenario YAML"
            )

        cursor = self._cursors.get(agent.name, 0)
        if cursor >= len(responses):
            raise FakeProviderError(
                f"agent {agent.name!r} was called {cursor + 1} times but only "
                f"{len(responses)} response(s) scripted; add another entry under "
                f"agent_scripts.{agent.name} in the scenario YAML"
            )

        scripted = responses[cursor]
        self._cursors[agent.name] = cursor + 1
        self._calls.append({"agent": agent.name, "prompt_len": len(rendered_prompt)})

        return AgentOutput(
            content=scripted.content,
            raw_response=scripted.raw_response,
        )

    async def validate_connection(self) -> bool:
        return True

    async def close(self) -> None:
        return None
