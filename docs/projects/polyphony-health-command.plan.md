# Polyphony Health Command Plan

## Overview

This document outlines the design and implementation plan for the `polyphony health` CLI command, which performs environment and configuration diagnostics for Polyphony CLI. The command checks .NET runtime version, AOT support, presence/version of the twig CLI, validity of `.polyphony-config/process-config.yaml`, SQLite availability and WAL mode, and YamlDotNet compatibility. Results are output as JSON using PolyphonyJsonContext, with actionable messages for each check.

## Diagnostic Checks
- .NET runtime version
- Runtime AOT support (RuntimeFeature.IsDynamicCodeSupported)
- twig CLI presence/version
- Validity of `.polyphony-config/process-config.yaml`
- SQLite availability and WAL mode
- YamlDotNet compatibility

## Output
- JSON output via PolyphonyJsonContext
- Exit code 4 (ExitCodes.HealthCheckFailed) on critical failures
- Actionable messages for each failed check

## Test Coverage
- Contract and unit tests for all checks and output scenarios
- Pinning tests for ExitCodes.HealthCheckFailed

## Acceptance Criteria
- Command runs and outputs JSON diagnostic results
- All checks are AOT-safe and use PolyphonyJsonContext
- PolyphonyJsonContext.cs updated to register new health result type
- Fails with ExitCodes.HealthCheckFailed on critical failures
- Actionable messages for each failed check
- Tests cover all checks and output scenarios
- Build passes with zero errors and warnings
- All existing tests pass; new tests cover changed behavior
