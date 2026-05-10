"""Harness driver entrypoint.

Usage::

    python -m harness.driver.run <scenario-dir>

Loads the scenario, constructs a ``FakeProvider`` over conductor's
existing provider seam, runs the workflow, captures the event trace,
checks expectations, and prints a JSON result document to stdout.

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
import traceback
from pathlib import Path
from typing import Any

from conductor.config.loader import load_workflow
from conductor.engine.workflow import WorkflowEngine
from conductor.events import WorkflowEventEmitter

from .fakes.provider import FakeProvider
from .scenario import Scenario, load_scenario
from .trace import TraceRecorder, check_expectations


def _repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


async def _run_scenario(scenario: Scenario) -> tuple[int, dict[str, Any]]:
    provider = FakeProvider(scripts=scenario.agent_scripts)
    emitter = WorkflowEventEmitter()
    recorder = TraceRecorder()
    recorder.subscribe(emitter)

    config = load_workflow(scenario.workflow_path)

    engine = WorkflowEngine(
        config=config,
        provider=provider,
        skip_gates=True,
        workflow_path=scenario.workflow_path,
        event_emitter=emitter,
    )

    workflow_output: dict[str, Any] | None = None
    error_text: str | None = None
    try:
        workflow_output = await engine.run(scenario.inputs)
    except Exception as exc:
        error_text = f"{type(exc).__name__}: {exc}\n{traceback.format_exc()}"

    failures = check_expectations(scenario.expected_trace, recorder, workflow_output)
    if error_text and not failures:
        failures.append(f"workflow raised: {error_text}")

    result: dict[str, Any] = {
        "scenario": scenario.name,
        "workflow": str(scenario.workflow_path.relative_to(_repo_root())),
        "passed": not failures,
        "failures": failures,
        "trace": recorder.to_dict(),
        "calls": provider.calls,
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
    args = parser.parse_args(argv)

    try:
        scenario = load_scenario(args.scenario_dir, _repo_root())
    except (FileNotFoundError, ValueError) as exc:
        _emit_result({"passed": False, "driver_error": str(exc)}, args.output_json)
        return 2

    try:
        exit_code, result = asyncio.run(_run_scenario(scenario))
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
