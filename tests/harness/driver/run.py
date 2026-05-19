"""Harness driver entrypoint.

Usage::

    python -m driver.run <scenario-dir>

Loads the scenario, constructs a ``FakeProvider`` over conductor's
existing provider seam, stages a per-scenario shim bin/ that intercepts
polyphony / twig / gh subprocess calls, runs the workflow, captures the
event trace, checks expectations, and prints a JSON result document.

Exit codes:
  0 — scenario passed
  1 — scenario failed (assertion mismatch)
  2 — driver error (fixture malformed, conductor exception, etc.)
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
import tempfile
import traceback
from pathlib import Path
from typing import Any

from conductor.config.loader import load_workflow
from conductor.engine.workflow import WorkflowEngine
import conductor.engine.workflow as engine_module
from conductor.events import WorkflowEventEmitter

from .fakes.provider import FakeProvider
from .scenario import Scenario, load_scenario
from .scripted_gate import ScriptedGateError, ScriptedGateHandler
from .shim_runtime import patched_environment, read_audit_log, stage_scenario_bin
from .trace import TraceRecorder, check_expectations


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


async def _run_scenario(scenario: Scenario, *, verbose: bool = False) -> tuple[int, dict[str, Any]]:
    provider = FakeProvider(scripts=scenario.agent_scripts)
    emitter = WorkflowEventEmitter()
    recorder = TraceRecorder()
    recorder.subscribe(emitter)

    config = load_workflow(scenario.workflow_path)

    # Install the scripted gate handler for both top-level and sub-workflow
    # engines. ``WorkflowEngine.__init__`` constructs its handler from the
    # module-level ``HumanGateHandler`` symbol; sub-workflows do the same.
    # Monkey-patching the symbol for the duration of ``engine.run()`` makes
    # the scripted handler the ONLY handler any engine instantiates this
    # run, so a gate in cascade-remedy.yaml or feature-pr.yaml resolves
    # against the same scenario ``gates:`` block as a gate in the top-level
    # workflow. See AB#3212 for the design notes.
    scripted_handler = ScriptedGateHandler(
        scripted=scenario.gates,
        strict=scenario.strict_gates,
    )

    def _handler_factory(
        skip_gates: bool = False,
        console: Any = None,
    ) -> ScriptedGateHandler:
        # Sub-workflow engines call ``HumanGateHandler(skip_gates=...)``.
        # We always return a handler bound to the same scripted entries
        # and the same ``_consumed`` set so end-of-run validation sees
        # every consumption across the whole engine tree.
        scripted_handler.console = console or scripted_handler.console
        return scripted_handler

    original_handler_cls = engine_module.HumanGateHandler

    workflow_output: dict[str, Any] | None = None
    error_text: str | None = None
    cli_calls: list[dict[str, str]] = []
    unused_gates: list[str] = []
    extra_failures: list[str] = []

    engine_module.HumanGateHandler = _handler_factory  # type: ignore[assignment,misc]
    try:
        engine = WorkflowEngine(
            config=config,
            provider=provider,
            skip_gates=True,
            workflow_path=scenario.workflow_path,
            event_emitter=emitter,
        )
        # Belt-and-braces: the factory ran during construction and produced
        # ``scripted_handler``; the explicit assignment keeps the wiring
        # obvious to a reader of the driver.
        engine.gate_handler = scripted_handler

        with tempfile.TemporaryDirectory(prefix=f"harness-{scenario.name}-") as tmp:
            bin_dir = Path(tmp) / "bin"
            shim_ctx = stage_scenario_bin(bin_dir, scenario.cli_scripts, verbose=verbose)
            with patched_environment(shim_ctx):
                try:
                    workflow_output = await engine.run(scenario.inputs)
                except ScriptedGateError as exc:
                    extra_failures.append(f"scripted gate error: {exc}")
                except Exception as exc:
                    error_text = f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}"
            cli_calls = read_audit_log(shim_ctx)
    finally:
        engine_module.HumanGateHandler = original_handler_cls  # type: ignore[assignment]

    declared = set(scenario.gates)
    consumed = scripted_handler.consumed_gates
    unused_gates = sorted(declared - consumed)

    failures = check_expectations(scenario.expected_trace, recorder, workflow_output)
    failures.extend(extra_failures)
    if unused_gates:
        failures.append(
            "unused gates declared in `gates:` but never reached: "
            f"{unused_gates} (typo, or scenario routes never hit these gates)"
        )
    if error_text and not failures:
        failures.append(f"workflow raised: {error_text}")

    result: dict[str, Any] = {
        "scenario": scenario.name,
        "workflow": str(scenario.workflow_path.relative_to(_repo_root())),
        "passed": not failures,
        "failures": failures,
        "trace": recorder.to_dict(),
        "agent_calls": provider.calls,
        "cli_calls": cli_calls,
        "workflow_output": workflow_output,
    }
    if error_text:
        result["error"] = error_text

    return (0 if not failures else 1, result)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Run a polyphony harness scenario.")
    parser.add_argument("scenario_dir", type=Path, help="Path to scenario directory.")
    parser.add_argument(
        "--output-json",
        type=Path,
        default=None,
        help=(
            "Write the scenario result JSON to this file instead of stdout. "
            "Recommended when the harness is invoked from Pester — conductor "
            "writes Rich-formatted output to stdout that interferes with "
            "ConvertFrom-Json."
        ),
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="Print build / setup chatter to stderr.",
    )
    args = parser.parse_args(argv)

    try:
        scenario = load_scenario(args.scenario_dir, _repo_root())
    except (FileNotFoundError, ValueError) as exc:
        _emit_result({"passed": False, "driver_error": str(exc)}, args.output_json)
        return 2

    try:
        exit_code, result = asyncio.run(_run_scenario(scenario, verbose=args.verbose))
    except Exception as exc:  # pragma: no cover - guarded for unknown failure
        _emit_result(
            {
                "passed": False,
                "driver_error": f"{type(exc).__name__}: {exc}",
                "traceback": traceback.format_exc(),
            },
            args.output_json,
        )
        return 2

    _emit_result(result, args.output_json)
    return exit_code


def _emit_result(result: dict[str, Any], output_path: Path | None) -> None:
    payload = json.dumps(result, default=str)
    if output_path is None:
        print(payload, flush=True)
    else:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_text(payload, encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
