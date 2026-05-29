# Project Context

- **Owner:** Daniel Green
- **Project:** Polyphony — type-agnostic SDLC routing engine and conductor workflow suite. .NET 11 CLI (`src/Polyphony/`) + conductor workflow YAMLs (`.conductor/registry/workflows/`) + PowerShell helpers (`scripts/`) + tests (xunit + Pester + Python harness).
- **Stack:** C# (.NET 11, ConsoleAppFramework, AOT JSON), conductor workflow YAML, PowerShell 7+, Python (harness), twig CLI (companion), git worktrees.
- **Created:** 2026-05-28

## Learnings

- 📌 Team formed 2026-05-28. Universe: Classical Composers (custom). My seat: Architect. Co-members: Beethoven (Mission), Brahms (Testability), Mahler (Conductor), Stravinsky (AI Agents), Mozart (.NET/C#), Liszt (PowerShell), Wagner (Workflow Author), Sibelius (Twig/ADO), Reich (Git/Worktrees) + Scribe + Ralph.
- 📌 Polyphony NEVER writes to ADO directly. All writes go through `twig set / state / sync / note`. This is the core layering invariant. See `docs/polyphony-architecture.md`.
- 📌 Platform abstraction (GitHub vs ADO PRs) lives in workflow YAML, NOT in C# interfaces. There is no `IPlatform` — the split is in `pr_platform_router` inline pwsh nodes. Verified by `git grep -l "IPlatform\|IProcessAdapter" src/Polyphony` returning nothing.
- 📌 Glossary at `docs/glossary.md` is authoritative. Forbidden terms (AB#3259): `apex`→`root`, `apex-driver` (top-level is `polyphony.yaml`), `tree-walker`, `primary_*`→`root_*`, `Primary*`→`Root*`, `wave`→`batch`, `cascade`→`restack`, `*_dispatch` suffix dropped, `terminal_*` prefix dropped. Lint-enforced via `lint-vocabulary.ps1`.
- 📌 2026-05-28: Participated in squad-wide initial concerns review (10-agent fan-out). Surfaced 3 top architectural concerns: verb output schema registry deferred, process-config state-name specificity, twig CLI response parsing fragility. Nominated short-term wins: backfill `[VerbResult]` attributes (Mozart), document twig response contract (Sibelius).
- 📌 2026-05-28: Designed on_error + notifications architectural integration. Key findings:
  - The polyphony-CLI-exit-0 problem is a SEAM decision (not a code style question). Recommended Option C: hybrid exit codes (exit-0 for domain outcomes, non-zero for infrastructure failures). ADR required before #536 retrofit.
  - "Notification" is dangerously overloaded (conductor node type + platespinner UX + polyphony payload). Proposed vocabulary: polyphony calls its payload a "domain signal" to avoid triple-overload. Glossary addition required.
  - Four network/process seams (twig, git, PR platform, ADO sync) share the same retry-then-abort pattern. on_error: Phase 1 makes this declarative. The 14 retry gates removed in #535 can be restored as automated retry chains.
  - PlateSpinner integration is a READ-ONLY seam (platespinner reads .events.jsonl; no write-back). Contract boundary is the .events.jsonl line shape. Low coupling, acceptable new seam.
  - Filed #541 and #542 on PolyphonyRequiem/polyphony for platespinner gaps (event-type handler + deep-link toast context).
  - SCOPE REFINED: The headline unlock is **gate compression** — `human_gate` → `(notification + script-poll-loop)`. Gates become rare (judgment-only). Pollable conditions (PR merged? review approved? CI green?) become script loops fronted by domain signals. PlateSpinner's role is pure one-way observer: read .events.jsonl, render CTA buttons, manage correlation lifecycle. NO write-back to conductor ever.
  - The domain signal envelope needs CTA fields (`cta_url`, `cta_kind`, `correlation_id`, `expires_at`) to support the user→workflow loop. PlateSpinner renders the CTA; user clicks; poll script detects resolution. Four-segment one-way loop.
  - Three ADRs required before implementation: (1) verb-error-boundary [P0], (2) domain-signal-envelope [P1], (3) gate-compression-pattern [P1].
  - 2026-05-28: Coordinated with Wagner's pattern catalogue (8 patterns in `.squad/decisions/inbox/wagner-on-error-notifications-patterns-2026-05-28T23-00-46Z.md`). Three asks resolved: (1) severity is 4-level (info/warning/error/critical) living on both NotificationTypeDef and wire envelope; (2) `{{ conductor.run_id }}` unknown — workaround via workflow input, Mahler to verify; (3) `type: wait` templated seconds unknown — fallback is fixed backoff or script-based sleep, Mahler to verify. Added `disposition` field to envelope for Wagner's Pattern 2/8. Confirmed no divergent contracts between top-down envelope and bottom-up patterns.

## Learnings — 2026-05-28

### Bach (Architect)

**Current focus:** on_error + notifications architectural design + platespinner integration  
**Status:** Completed comprehensive design review + 3 ADR proposals

**Session round outcomes:**
- ✅ **2026-05-29T13:41:04-07:00:** Seed-manifest ADR (PR #546) merged to polyphony main at 619ce7c.
- ✅ Wrote gate-compression headline: `human_gate` → `(notification + script-poll-loop)` for observable conditions
- ✅ Identified 3 critical seams (polyphony verb error emission, notifications→platespinner, conductor feature gaps)
- ✅ Proposed 3 ADRs: polyphony-verb-error-boundary (P0), domain-signal-envelope (P1), gate-compression-pattern (P1)
- ✅ Created platespinner gap issues #541 (CTA-aware rendering) + #542 (deep-link context)
- ✅ Documented vocabulary alerts for Beethoven (domain signal, gate compression, CTA, correlation ID, disposition)
- **Critical ruling:** Option C (hybrid exit codes) for verb-error-boundary; severity + run_id + templated wait.seconds as Bach envelope asks

**Next moves:**
- Wait for Daniel approval on 3 ADRs before implementation
- Coordinate with Mahler on run_id + wait.seconds verification
- Write `domain-signal-envelope.md` ADR once approved

---

### 2026-05-28T23:43-14Z — Inbox round (Scribe merge)
- ✅ 3 ADRs merged to decisions.md (domain-signal-envelope, gate-compression-pattern, polyphony-verb-error-boundary)
- ✅ Platespinner handoff doc captured
- ✅ User vocabulary directive (domain signal + emit:) recorded
- Ready for downstream implementation once Daniel approves

## Learnings — 2026-05-29

### Bach (Architect) — Seeding Plan Architecture (PR #535 Q1)

- ✅ Responded to Daniel's Q1 directive on durable seed state across retries
- ✅ Proposed "seed manifest" concept (NOT "seeding plan") — a frozen record of planner intent persisted at `.polyphony/state/{rootId}/seed-manifest.json`
- ✅ Defined reconciliation primitive: type + parent linkage + title-normalized hash
- ✅ Recommended new ADR (`seed-manifest-as-durable-state.md`), not extending existing ADRs
- ✅ PR #535 survives as rebase — gate removal correct, manifest-aware seeder added
- Key architectural stance: manifest is "desired state" (like Terraform plan), ADO is "actual state." Reconciliation is the diff. No new subsystem — just a verb output file.
- Push-back delivered: the concept is valid but must stay thin (one file, 5 fields, replaced on replan). If it grows storage/versioning/queryability, it's over-engineered.

### Bach (Architect) — ADR Delivery: seed-manifest-as-durable-state

- ✅ ADR written to `polyphony/docs/decisions/seed-manifest-as-durable-state.md`
- ✅ Synthesized four-lens debate: Bach (architecture), Mahler (conductor engine), Mozart (verb error circuit), Sibelius (platform reality), plus Beethoven's terminal semantics analysis
- ✅ Key design decisions encoded as accepted:
  - Reconciliation primitive: `polyphony:plan-child-id` marker (NOT title hash — Sibelius confirmed the marker embeds atomically with creation and `BuildIndexes` already uses it)
  - `plan_generation` is a rich chain object (id + parent + cause + created_at), not a monotonic counter — tracks why a generation was created and enables orphan detection
  - Items carry `introduced_in: "gen-N"` for orphan detection at renegotiation time
  - Retry circuit lives INSIDE the seeding script (Mahler confirmed `max_attempts` not available for `type: script` nodes)
  - 3 attempts on codes 3 (twig unavailable) and 5 (ADO unreachable including 401/403)
  - Code 1 = unclassified crash = permanent, no retry
  - New conductor terminal `seeding_blocked` (NOT `workflow_abandoned` — Beethoven's analysis confirmed all 4 routes into `workflow_abandoned` are operator-volitional; infra exhaustion is not)
  - GitHub out of scope; no `platform` field on manifest
  - PR #535 survives as rebase: gate removal correct, manifest + reconciliation added on top
- Title hash approach (Bach's prior pass) superseded by Sibelius's stronger finding: the existing marker primitive handles cross-run reconciliation without a new comparison surface
- 5 open questions documented for team follow-up: orphan disposition workflow, `seeding_blocked` re-trigger path (Wagner), manifest schema version field, seeder idempotency across renegotiation, `seeding_complete` kind vocabulary addition to domain-signal-envelope ADR

## Learnings — 2026-05-28 (Round 2)

### Bach (Architect) — ADR Delivery + Platespinner Handoff

**All four deliverables shipped:**

1. **Platespinner handoff prompt** at `.squad/handoffs/platespinner-gap-work.md`  
   - Fully self-contained for a fresh agent session against `conductor-platespinner`  
   - Includes: full gap issue text (#541 CTA rendering, #542 deep-link context), wire format
     for `.notifications.jsonl` with 3 realistic example payloads, one-way contract reminder,
     dogfood install path, definition of done with checkboxes, explicit out-of-scope list  
   - Key discovery: platespinner's current notifications are state-derived (from SSE run data);
     domain signals are a NEW parallel channel from `*.notifications.jsonl` files. The handoff
     makes this explicit so the receiving agent doesn't conflate the two pipelines.

2. **ADR: polyphony-verb-error-boundary** at `polyphony/docs/decisions/polyphony-verb-error-boundary.md`  
   - Option C hybrid: exit 0 for domain outcomes, non-zero for infrastructure failures  
   - Exit code catalogue seeded (codes 0–6), Mozart owns per-verb assignments  
   - Invariant: exit 0 never means "nothing happened" — stdout JSON always present

3. **ADR: domain-signal-envelope** at `polyphony/docs/decisions/domain-signal-envelope.md`  
   - Polyphony owns `payload` inside conductor's notification envelope  
   - Required: kind, severity, title, message  
   - Optional: cta_url, cta_kind, correlation_id, expires_at, disposition, details  
   - Full wire example + workflow YAML declaration included  
   - One-way contract stated as an invariant  

4. **ADR: gate-compression-pattern** at `polyphony/docs/decisions/gate-compression-pattern.md`  
   - Compression rule: observable conditions → emit + poll; judgment gates → stay human_gate  
   - Canonical 3-step YAML pattern documented  
   - 4 open asks surfaced for Daniel (poll backoff, expiry behavior, resolved signal convention,
     max poll cap)

**Design moves worth remembering:**
- Conductor notification envelope has `data.payload` as the polyphony-owned zone.
  The outer fields (schema_id, emission_id, correlation) are conductor-owned and must
  not be redefined by polyphony.
- The `.notifications.jsonl` file is SEPARATE from `.events.jsonl` — same conductor temp dir,
  different file. Platespinner's current code only reads `*.events.jsonl`. The domain signal
  channel requires a new reader.
- Wagner's dogfood test confirmed: current field names are `type: notification` / `notification:`,
  NOT yet `type: emit` / `emit:`. Rename is pending PR #213 commit 27006af. Platespinner
  should build against the envelope format (transparent to rename), not the YAML syntax.
- The `polyphony/docs/decisions/` path EXISTS in the separate polyphony clone at
  `C:\Users\dangreen\projects\polyphony\docs\decisions\`. ADRs were written there directly.
