# Dogfood Conductor — Skill

## Purpose

How to build, install, and refresh a local conductor dogfood branch that combines two in-flight PRs ahead of upstream merge. Use this when polyphony needs a conductor feature that hasn't released yet.

---

## Prerequisites

- `C:\Users\dangreen\projects\conductor` — main conductor checkout with `origin` = microsoft/conductor, `fork` = PolyphonyRequiem/conductor
- Feature worktrees exist (one per PR branch)
- Python 3.12+ with pip on PATH

---

## Step 1 — Establish baseline

```powershell
cd C:\Users\dangreen\projects\conductor
git fetch --all --prune

# Latest tag:
git tag --sort=-version:refname | Select-Object -First 3

# Map each feature branch to its merge-base with origin/main:
git merge-base <feature-branch> origin/main
```

**Choice rule:** If any feature branch is already based on a commit between two tags (not exactly on a tag), use the latest common ancestor of all feature branches as the dogfood base. This avoids semantic conflicts from combining two branches that each independently diverged from a newer base.

---

## Step 2 — Rebase feature branches onto latest tag

For each feature branch:
```powershell
cd <worktree>
git fetch origin
$env:GIT_EDITOR = "true"   # suppress interactive editor during rebase
git rebase v0.X.Y
```

**Conflict assessment:** Before resolving any conflict, classify it:
- **Mechanical** (enum unions, adjacent field additions, import blocks): safe to combine both sides
- **Semantic** (logic restructuring, behavioral changes, test-behavior assertions): STOP, document, escalate

For mechanical schema.py conflicts (AgentDef.type Literal union additions):
```python
# Combine both sets of type values:
type: Literal["existing1", "existing2", "new-from-branch-A", "new-from-branch-B"] | None = None
```

Push if clean:
```powershell
git push fork <branch> --force-with-lease
```

If rebase has semantic conflicts, abort and note in decision doc:
```powershell
git rebase --abort
```

---

## Step 3 — Create dogfood worktree

```powershell
cd C:\Users\dangreen\projects\conductor
$base = "<efa520f-or-whichever-common-ancestor>"
git worktree add C:\Users\dangreen\projects\conductor-dogfood -b dogfood/on-error+notifications $base
```

---

## Step 4 — Merge feature branches

```powershell
cd C:\Users\dangreen\projects\conductor-dogfood
$env:GIT_EDITOR = "true"

# Merge the larger/more-invasive branch first:
git merge <branch-A> --no-ff -m "Merge <branch-A> into dogfood"

# If second branch can't merge cleanly due to conflicts with the first,
# cherry-pick the key commit(s) instead of merging the whole branch:
git cherry-pick <specific-commit-sha>
```

**Prefer cherry-pick over merge** when:
- The second feature branch includes upstream commits that conflict with the first (e.g., it was rebased onto a newer tag that the first branch's commits conflict with)
- You only need one or a few commits from the branch

---

## Step 5 — Install and verify

```powershell
cd C:\Users\dangreen\projects\conductor-dogfood
pip install -e .
conductor --version

# Run feature-specific tests:
$py = "C:\Users\dangreen\AppData\Local\Python\pythoncore-3.14-64\python.exe"
& $py -m pytest tests/test_engine/test_error_routing.py tests/test_config/test_notifications.py -q
```

---

## Step 6 — Refresh when upstream PRs update

When a PR gets new commits:
1. Fetch the updated branch in its worktree
2. Re-run the rebase (or skip if no new upstream changes)
3. In the dogfood worktree, reset the affected cherry-pick:
   ```powershell
   git reset --hard <prior-merge-sha>  # before the cherry-pick
   git cherry-pick <new-feature-sha>
   pip install -e .
   ```

---

## Common pitfalls

| Symptom | Cause | Fix |
|---|---|---|
| `git rebase --continue` hangs | Interactive editor waiting for commit message | Set `$env:GIT_EDITOR = "true"` before `rebase --continue` |
| Tests fail with "No module named 'conductor'" | Wrong Python on PATH | Use explicit path: `$py = "C:\...\python.exe"; & $py -m pytest ...` |
| `pytest.mark.asyncio` warnings / test failures | `pytest-asyncio` not installed | `pip install pytest-asyncio` |
| Schema.py has `<<<<<<< HEAD` marker left after edit | Partial conflict resolution — the `<<<<<<< HEAD` at the top of a block was left when inner `=======`/`>>>>>>>` were removed | Search explicitly for `<<<<<<` and remove orphaned markers |
| `agent_outputs.get()` vs subscript access conflict | Semantic conflict: error-routing changed None-output semantics; set-step relies on `None` seed value | Escalate — requires PR author decision on sentinel pattern |
