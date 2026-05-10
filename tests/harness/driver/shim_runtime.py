"""Build / discover the harness shim binary and stage a per-scenario bin/.

The shim is a .NET console app under ``tests/harness/shim`` that
impersonates polyphony / twig / gh based on a JSON manifest. The driver
copies it once per scenario into an ephemeral bin directory under the
shim names, prepends that directory to ``PATH`` for the duration of the
run, and points the shim at a manifest written from the scenario's
``cli_scripts`` block.

Build is cached: if the shim binary's mtime is newer than every source
file in the project, ``dotnet publish`` is skipped. First-run cost on a
warm SDK is ~5–10 s; subsequent runs are free.
"""

from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
from contextlib import contextmanager
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Iterator


SHIM_NAMES = ("polyphony", "twig", "gh")


@dataclass
class CliScript:
    """One scripted CLI invocation response."""

    command: str
    args: list[str]
    stdout: str = ""
    stderr: str = ""
    exit_code: int = 0

    def to_manifest_entry(self) -> dict[str, Any]:
        return {
            "command": self.command,
            "args": list(self.args),
            "stdout": self.stdout,
            "stderr": self.stderr,
            "exit_code": int(self.exit_code),
        }


@dataclass
class ShimContext:
    """Files / env tweaks the driver needs to set up the shim for a scenario."""

    bin_dir: Path
    manifest_path: Path
    audit_log_path: Path
    extra_env: dict[str, str] = field(default_factory=dict)


def project_dir() -> Path:
    return Path(__file__).resolve().parent.parent / "shim" / "Polyphony.HarnessShim"


def _runtime_identifier() -> str:
    """Pick a .NET RID for self-contained publish on the host OS."""
    if os.name == "nt":
        return "win-x64"
    if sys.platform == "darwin":
        return "osx-x64"
    return "linux-x64"


def published_binary_path() -> Path:
    """Where ``dotnet publish`` lands the shim binary on this OS."""
    suffix = ".exe" if os.name == "nt" else ""
    return (
        project_dir()
        / "bin"
        / "Release"
        / "publish"
        / _runtime_identifier()
        / f"Polyphony.HarnessShim{suffix}"
    )


def ensure_built(verbose: bool = False) -> Path:
    """Build the shim if its binary is older than any source file.

    Returns the path to the published binary. Self-contained single-file
    publish so the binary can be copied freely to ephemeral bin/ dirs
    without needing an adjacent .dll or a .NET runtime on the target.
    """
    project = project_dir()
    binary = published_binary_path()

    needs_build = True
    if binary.is_file():
        binary_mtime = binary.stat().st_mtime
        sources = list(project.glob("**/*.cs")) + [project / "Polyphony.HarnessShim.csproj"]
        latest_source_mtime = max((p.stat().st_mtime for p in sources if p.is_file()), default=0.0)
        needs_build = latest_source_mtime > binary_mtime

    if not needs_build:
        return binary

    rid = _runtime_identifier()
    publish_dir = project / "bin" / "Release" / "publish" / rid
    cmd = [
        "dotnet",
        "publish",
        str(project),
        "-c",
        "Release",
        "-r",
        rid,
        "--self-contained",
        "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-o",
        str(publish_dir),
        "--nologo",
        "/v:quiet",
    ]
    if verbose:
        print(f"[harness] building shim: {' '.join(cmd)}", file=sys.stderr)
    result = subprocess.run(cmd, capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(
            f"failed to publish harness shim (exit {result.returncode}):\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}"
        )

    if not binary.is_file():
        raise RuntimeError(
            f"shim publish reported success but binary missing at {binary}\n"
            f"stdout: {result.stdout}"
        )

    return binary


def stage_scenario_bin(
    bin_dir: Path,
    cli_scripts: list[CliScript],
    *,
    verbose: bool = False,
) -> ShimContext:
    """Stage a per-scenario bin/ with shim copies + a manifest.

    The directory is recreated on every call so stale shim copies from a
    previous run can never bleed into the current scenario.
    """
    binary = ensure_built(verbose=verbose)
    bin_dir.mkdir(parents=True, exist_ok=True)

    suffix = ".exe" if os.name == "nt" else ""
    for shim_name in SHIM_NAMES:
        target = bin_dir / f"{shim_name}{suffix}"
        if target.exists():
            target.unlink()
        shutil.copy2(binary, target)
        if os.name != "nt":
            target.chmod(0o755)

    manifest_path = bin_dir / "manifest.json"
    audit_log_path = bin_dir / "audit.log"

    manifest = {
        "responses": [s.to_manifest_entry() for s in cli_scripts],
        "audit_log": str(audit_log_path),
    }
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")

    # Empty audit so each run starts clean.
    audit_log_path.write_text("", encoding="utf-8")

    return ShimContext(
        bin_dir=bin_dir,
        manifest_path=manifest_path,
        audit_log_path=audit_log_path,
        extra_env={"POLYPHONY_HARNESS_MANIFEST": str(manifest_path)},
    )


@contextmanager
def patched_environment(ctx: ShimContext) -> Iterator[None]:
    """Prepend the staged bin/ to PATH and set the manifest env var."""
    saved = {key: os.environ.get(key) for key in ("PATH", *ctx.extra_env.keys())}
    sep = os.pathsep
    os.environ["PATH"] = str(ctx.bin_dir) + sep + os.environ.get("PATH", "")
    for key, value in ctx.extra_env.items():
        os.environ[key] = value
    try:
        yield
    finally:
        for key, value in saved.items():
            if value is None:
                os.environ.pop(key, None)
            else:
                os.environ[key] = value


def read_audit_log(ctx: ShimContext) -> list[dict[str, str]]:
    """Parse the shim's audit log into structured records."""
    if not ctx.audit_log_path.is_file():
        return []
    records: list[dict[str, str]] = []
    for line in ctx.audit_log_path.read_text(encoding="utf-8").splitlines():
        if not line.strip():
            continue
        parts = line.split("\t", 2)
        if len(parts) < 2:
            continue
        records.append(
            {
                "timestamp": parts[0],
                "command": parts[1],
                "args": parts[2] if len(parts) >= 3 else "",
            }
        )
    return records
