"""Scenario YAML loader.

A scenario is a directory containing one ``scenario.yaml`` file. The YAML
shape is intentionally minimal:

    workflow: <repo-relative path>
    inputs:
      key: value
    agent_scripts:
      <agent_name>:
        - content: { ... }   # one or more scripted outputs
    cli_scripts:
      - command: polyphony
        args: [plan, classify-stale-descendants]
        stdout: "{...json...}"
        exit_code: 0
    expected_trace:
      agents_executed: [<name>, ...]   # ordered subsequence
      reached_terminal: true
      output_contains:                  # optional partial match
        key: value

The loader is forgiving on optional fields and strict on required ones —
a malformed scenario fails fast with a useful message.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import ruamel.yaml

from .fakes.provider import ScriptedResponse
from .shim_runtime import CliScript


def _load_yaml(text: str) -> object:
    yaml = ruamel.yaml.YAML(typ="safe", pure=True)
    return yaml.load(text)


@dataclass
class ExpectedTrace:
    agents_executed: list[str] = field(default_factory=list)
    reached_terminal: bool = True
    output_contains: dict[str, Any] = field(default_factory=dict)


@dataclass
class Scenario:
    name: str
    directory: Path
    workflow_path: Path
    inputs: dict[str, Any]
    agent_scripts: dict[str, list[ScriptedResponse]]
    cli_scripts: list[CliScript]
    expected_trace: ExpectedTrace


def load_scenario(scenario_dir: Path, repo_root: Path) -> Scenario:
    """Load a scenario from ``<scenario_dir>/scenario.yaml``."""
    scenario_dir = scenario_dir.resolve()
    scenario_file = scenario_dir / "scenario.yaml"
    if not scenario_file.is_file():
        raise FileNotFoundError(f"scenario file not found: {scenario_file}")

    raw = _load_yaml(scenario_file.read_text(encoding="utf-8"))
    if not isinstance(raw, dict):
        raise ValueError(f"scenario file must be a YAML mapping: {scenario_file}")

    workflow_rel = raw.get("workflow")
    if not workflow_rel:
        raise ValueError(f"scenario missing required field 'workflow': {scenario_file}")

    workflow_path = (repo_root / workflow_rel).resolve()
    if not workflow_path.is_file():
        raise FileNotFoundError(f"workflow YAML not found: {workflow_path}")

    inputs = raw.get("inputs") or {}
    if not isinstance(inputs, dict):
        raise ValueError(f"'inputs' must be a mapping: {scenario_file}")

    scripts_raw = raw.get("agent_scripts") or {}
    if not isinstance(scripts_raw, dict):
        raise ValueError(f"'agent_scripts' must be a mapping: {scenario_file}")

    agent_scripts: dict[str, list[ScriptedResponse]] = {}
    for agent_name, entries in scripts_raw.items():
        if not isinstance(entries, list) or not entries:
            raise ValueError(
                f"agent_scripts.{agent_name} must be a non-empty list: {scenario_file}"
            )
        agent_scripts[agent_name] = [
            ScriptedResponse(
                content=entry.get("content", {}),
                raw_response=entry.get("raw_response"),
            )
            for entry in entries
        ]

    cli_raw = raw.get("cli_scripts") or []
    if not isinstance(cli_raw, list):
        raise ValueError(f"'cli_scripts' must be a list: {scenario_file}")
    cli_scripts: list[CliScript] = []
    for idx, entry in enumerate(cli_raw):
        if not isinstance(entry, dict):
            raise ValueError(f"cli_scripts[{idx}] must be a mapping: {scenario_file}")
        command = entry.get("command")
        if not command or not isinstance(command, str):
            raise ValueError(
                f"cli_scripts[{idx}] missing required 'command' string: {scenario_file}"
            )
        args = entry.get("args") or []
        if not isinstance(args, list) or not all(isinstance(a, str) for a in args):
            raise ValueError(
                f"cli_scripts[{idx}].args must be a list of strings: {scenario_file}"
            )
        cli_scripts.append(
            CliScript(
                command=command,
                args=list(args),
                stdout=str(entry.get("stdout") or ""),
                stderr=str(entry.get("stderr") or ""),
                exit_code=int(entry.get("exit_code") or 0),
            )
        )

    expected_raw = raw.get("expected_trace") or {}
    expected = ExpectedTrace(
        agents_executed=list(expected_raw.get("agents_executed") or []),
        reached_terminal=bool(expected_raw.get("reached_terminal", True)),
        output_contains=dict(expected_raw.get("output_contains") or {}),
    )

    return Scenario(
        name=scenario_dir.name,
        directory=scenario_dir,
        workflow_path=workflow_path,
        inputs=inputs,
        agent_scripts=agent_scripts,
        cli_scripts=cli_scripts,
        expected_trace=expected,
    )
