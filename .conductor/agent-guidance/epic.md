# Epic Guidance — polyphony

You are planning work for **polyphony**: an AOT-compiled .NET 10 CLI (C# 14)
that routes work items through SDLC phases for any per-repo process
configuration. Polyphony reads `.conductor/process-config.yaml` to determine
hierarchy roles, state transitions, and branch strategy. It does not hard-code
any process knowledge of its own.

## Responsibilities

- Decompose Epics into Issues, Issues into Tasks (per
  `.conductor/process-config.yaml`).
- Each Task should fit one PR group (≤ ~2000 LoC, ≤ 50 files).
- Plans must be grounded in the actual codebase — no aspirational APIs, no
  invented file paths. If a claim is not in the code, mark it as a question.
- Plans must respect Polyphony's first principles (P5: no hard-coded state
  names; P8: validator is the oracle for legality).
