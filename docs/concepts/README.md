---
doc_type: index
status: active
synopsis: Index for concept docs — architecture, worktree model, agent failure modes.
---

# Concepts

Explanation docs for polyphony. Diátaxis "explanation" quadrant: understanding-oriented; the "why" behind the system.

If you are looking for stable reference material (the "what"), see [`../reference/`](../reference/README.md). If you are doing a task, see [`../guides/`](../guides/README.md).

| Doc | What it explains |
|---|---|
| [`polyphony-architecture.md`](polyphony-architecture.md) | Layering, the three-vocabulary contract, the platform-abstraction seam, and end-to-end data flow for a typical operation. Read this *before* changing any workflow YAML, helper script, or CLI verb. |
| [`per-run-worktree-layout.md`](per-run-worktree-layout.md) | Operator-facing reference for the per-root worktree contract under `<runs_root>/root-{N}/`. Both vanilla `git clone` and bare-repo layouts are first-class. |
| [`polyphony-agent-failure-modes.md`](polyphony-agent-failure-modes.md) | Postmortem of six concrete AI-agent failure modes when working on polyphony, each linked to the doc that would have prevented it. Calibration reading. |
