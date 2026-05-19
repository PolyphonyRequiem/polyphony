"""Trace recorder + assertion engine.

Subscribes to a conductor ``WorkflowEventEmitter`` and accumulates the
event stream. After the workflow run completes, ``check_expectations``
walks the recorded events against the scenario's ``expected_trace`` and
returns a list of human-readable failure messages (empty list = pass).

Assertion philosophy: **ordered subsequence**, not equality. The scenario
declares which agents must be executed in what order; events from agents
the scenario doesn't care about are ignored. This keeps scenarios robust
to incidental workflow changes (a new lifecycle event, a new metadata
field) while still catching real route divergences.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any

from conductor.events import WorkflowEvent

from .scenario import ExpectedTrace


@dataclass
class TraceRecorder:
    events: list[WorkflowEvent] = field(default_factory=list)

    def subscribe(self, emitter: Any) -> None:
        emitter.subscribe(self.events.append)

    @property
    def agents_executed(self) -> list[str]:
        """Ordered list of agent names whose execution started."""
        return self._collect_names("agent_started")

    @property
    def scripts_executed(self) -> list[str]:
        """Ordered list of script-node names that started executing.

        Lets scenarios assert which terminal/branch script ran without
        leaning on output_contains, e.g. proving closed_unmerged_emitter
        fired vs. already_merged_emitter.
        """
        return self._collect_names("script_started")

    @property
    def gates_presented(self) -> list[str]:
        """Ordered list of human-gate node names that were presented.

        Lets scenarios assert that a specific gate fired (e.g. that
        ``revise_cap_gate`` was reached after the no-commit fast-fail) even
        when ``--skip-gates`` auto-picks the first option and the workflow
        keeps walking.
        """
        return self._collect_names("gate_presented")

    def _collect_names(self, event_type: str) -> list[str]:
        names: list[str] = []
        for event in self.events:
            if event.type != event_type:
                continue
            name = (
                event.data.get("agent_name")
                or event.data.get("agent")
                or event.data.get("name")
            )
            if isinstance(name, str):
                names.append(name)
        return names

    @property
    def reached_terminal(self) -> bool:
        for event in self.events:
            if event.type in {"workflow_completed", "workflow_finished"}:
                return True
        return False

    def to_dict(self) -> dict[str, Any]:
        return {
            "event_count": len(self.events),
            "agents_executed": self.agents_executed,
            "scripts_executed": self.scripts_executed,
            "gates_presented": self.gates_presented,
            "reached_terminal": self.reached_terminal,
            "event_types": [event.type for event in self.events],
        }


def check_expectations(
    expected: ExpectedTrace,
    recorder: TraceRecorder,
    workflow_output: dict[str, Any] | None,
) -> list[str]:
    """Return a list of failure messages; empty list means all checks passed."""
    failures: list[str] = []

    actual_agents = recorder.agents_executed
    if not _is_ordered_subsequence(expected.agents_executed, actual_agents):
        failures.append(
            "agents_executed expected (ordered subsequence) "
            f"{expected.agents_executed!r}, actual {actual_agents!r}"
        )

    actual_scripts = recorder.scripts_executed
    if not _is_ordered_subsequence(expected.scripts_executed, actual_scripts):
        failures.append(
            "scripts_executed expected (ordered subsequence) "
            f"{expected.scripts_executed!r}, actual {actual_scripts!r}"
        )

    actual_gates = recorder.gates_presented
    if not _is_ordered_subsequence(expected.gates_presented, actual_gates):
        failures.append(
            "gates_presented expected (ordered subsequence) "
            f"{expected.gates_presented!r}, actual {actual_gates!r}"
        )

    if expected.reached_terminal and not recorder.reached_terminal:
        failures.append(
            "expected workflow to reach a terminal but no "
            "workflow_completed/workflow_finished event was observed"
        )

    if expected.output_contains:
        if workflow_output is None:
            failures.append("expected output_contains assertions but workflow output was None")
        else:
            for key, expected_value in expected.output_contains.items():
                if key not in workflow_output:
                    failures.append(f"output_contains: missing key {key!r}")
                elif workflow_output[key] != expected_value:
                    failures.append(
                        f"output_contains: {key!r} expected {expected_value!r}, "
                        f"got {workflow_output[key]!r}"
                    )

    return failures


def _is_ordered_subsequence(needle: list[str], haystack: list[str]) -> bool:
    if not needle:
        return True
    cursor = 0
    for item in haystack:
        if item == needle[cursor]:
            cursor += 1
            if cursor == len(needle):
                return True
    return False
