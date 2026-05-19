"""Scripted ``human_gate`` handler for harness scenarios (AB#3212).

Subclass of conductor's :class:`HumanGateHandler` that resolves gates
from a scenario-declared mapping ``{gate_name: option_value}`` rather
than the default ``--skip-gates`` behaviour of auto-picking
``options[0]``. Enables harness coverage of every defense path that
ROUTES to a human gate (mismatch / abort / force / retry), not just the
first-option path.

Behaviour matrix:

* gate is in ``scripted`` and the named ``value`` exists →
  route via the matching option, with optional ``additional_input``.
* gate is in ``scripted`` but the named ``value`` is unknown →
  raise :class:`ScriptedGateError` (mis-typed entry).
* gate is NOT in ``scripted`` and ``strict`` is True →
  raise :class:`ScriptedGateError` (undeclared gate; satisfies AB#3212
  AC #2 "no silent timeouts on undeclared gates").
* gate is NOT in ``scripted`` and ``strict`` is False →
  fall back to ``options[0]`` (preserves legacy ``--skip-gates``
  semantics so the eleven pre-AB#3212 scenarios stay green).

The handler does not emit any conductor events itself —
``WorkflowEngine`` emits ``gate_presented`` before invoking the handler
and ``gate_resolved`` after it returns, so trace assertions in
:mod:`driver.trace` continue to work unchanged.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any

from conductor.gates.human import GateResult, HumanGateHandler

if TYPE_CHECKING:
    from pathlib import Path

    from conductor.config.schema import AgentDef


@dataclass(frozen=True)
class ScriptedGateEntry:
    """One scenario-declared gate response.

    ``value`` matches the ``value`` field on a :class:`GateOption` (the
    same field the dashboard's WebSocket gate-response protocol uses).
    ``additional_input`` is forwarded as :attr:`GateResult.additional_input`
    when the chosen option has a ``prompt_for`` field — e.g.
    ``open_questions_gate`` (option ``answer``, ``prompt_for: answers``).
    """

    value: str
    additional_input: dict[str, str] = field(default_factory=dict)


class ScriptedGateError(RuntimeError):
    """Raised when a scripted-gate scenario hits an unexpected gate.

    Wrapped in :class:`RuntimeError` so the harness driver's existing
    ``except Exception`` in :func:`driver.run._run_scenario` converts it
    into a structured failure rather than letting it propagate as an
    untyped traceback.
    """


class ScriptedGateHandler(HumanGateHandler):
    """``HumanGateHandler`` that resolves gates from a scripted mapping.

    Constructed once per scenario in :mod:`driver.run` and assigned to
    every :class:`WorkflowEngine` instance the run creates (top-level and
    sub-workflow). See module docstring for the resolution matrix.
    """

    def __init__(
        self,
        scripted: dict[str, ScriptedGateEntry] | None = None,
        strict: bool = False,
        console: Any = None,
        skip_gates: bool = False,
    ) -> None:
        # ``skip_gates`` is accepted only to match the parent signature
        # — sub-workflow engines call ``HumanGateHandler(skip_gates=...)``
        # via the patched class symbol, and we forward it so any future
        # parent behaviour gated on it (e.g. logging) stays consistent.
        # Our own decision matrix ignores it: scripted entries win, strict
        # decides the undeclared-gate fallback.
        super().__init__(console=console, skip_gates=skip_gates)
        self._scripted: dict[str, ScriptedGateEntry] = scripted or {}
        self._strict = strict
        self._consumed: set[str] = set()

    @property
    def consumed_gates(self) -> set[str]:
        """Names of scripted gates that were actually presented and resolved.

        Used by :mod:`driver.run` to assert at end-of-run that every
        scripted entry was consumed (catches typos in ``gates:`` for
        gates the workflow never reaches).
        """
        return set(self._consumed)

    async def handle_gate(  # type: ignore[override]
        self,
        agent: AgentDef,
        context: dict[str, Any],
        base_dir: Path | None = None,
    ) -> GateResult:
        # Render the prompt so the scenario JSON output captures the
        # final prompt text (helps debugging when a route mismatches).
        # We don't display it — the harness runs headless.
        del context, base_dir  # rendering happens upstream of routing

        if not agent.options:
            return await super().handle_gate(agent, context={}, base_dir=None)

        entry = self._scripted.get(agent.name)
        if entry is not None:
            for opt in agent.options:
                if opt.value == entry.value:
                    self._consumed.add(agent.name)
                    return GateResult(
                        selected_option=opt,
                        route=opt.route,
                        additional_input=dict(entry.additional_input),
                    )
            available = [o.value for o in agent.options]
            raise ScriptedGateError(
                f"gate '{agent.name}' has no option with value={entry.value!r}; "
                f"available values: {available}. "
                "Fix the scenario's `gates:` block."
            )

        if self._strict:
            available = [o.value for o in agent.options]
            raise ScriptedGateError(
                f"undeclared gate '{agent.name}' reached under strict_gates=true; "
                f"declare it in the scenario's `gates:` block "
                f"(available option values: {available})."
            )

        # Lenient back-compat: same as parent's ``skip_gates`` path.
        return self._auto_select(agent.options[0])
