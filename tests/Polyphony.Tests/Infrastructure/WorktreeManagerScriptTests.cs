using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure;

/// <summary>
/// Pins the JSON envelope shape produced by
/// <c>.conductor/registry/scripts/worktree-manager.ps1</c> — the
/// per-item worktree spawner/teardown helper for the apex-driver
/// dispatch loop.
/// </summary>
/// <remarks>
/// The apex-driver fans work-items out across waves and dispatches each
/// item into a per-item git worktree so multiple lifecycle sub-workflows
/// can run in parallel. This script's envelope is the only contract
/// between the script and the workflow; tests pin both happy paths and
/// the error cases so a contract drift surfaces in CI.
///
/// Tests are skipped when <c>pwsh</c> is not on PATH (e.g., on a CI
/// runner without PowerShell 7).
/// </remarks>
[Trait("Category", "Slow")] // see #286 — forks pwsh per test
public sealed class WorktreeManagerScriptTests
{
    private static readonly string ScriptPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            ".conductor", "registry", "scripts", "worktree-manager.ps1"));

    private static bool PwshAvailable
    {
        get
        {
            try
            {
                using var probe = new Process();
                probe.StartInfo = new ProcessStartInfo
                {
                    FileName = "pwsh",
                    Arguments = "-NoProfile -Command \"$PSVersionTable.PSVersion.Major\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                probe.Start();
                probe.WaitForExit();
                return probe.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunScriptAsync(string args, string? workingDirectory = null)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            Arguments = $"-NoProfile -File \"{ScriptPath}\" {args}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (workingDirectory is not null)
        {
            process.StartInfo.WorkingDirectory = workingDirectory;
        }
        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Disposable git repository scaffolded in a temp directory with a
    /// single commit. Used by branch-existence tests so the script can
    /// be exercised end-to-end without polluting the polyphony repo.
    /// </summary>
    private sealed class TempGitRepo : IDisposable
    {
        public string RepoPath { get; }
        public string WorktreeRoot { get; }

        public TempGitRepo()
        {
            var temp = Path.Combine(Path.GetTempPath(), $"polyphony-wt-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            WorktreeRoot = Path.Combine(temp, "worktrees");
            Directory.CreateDirectory(WorktreeRoot);
            RepoPath = Path.Combine(temp, "repo");
            Directory.CreateDirectory(RepoPath);

            RunGit(RepoPath, "init", "-q", "-b", "main");
            RunGit(RepoPath, "config", "user.email", "test@example.com");
            RunGit(RepoPath, "config", "user.name", "Test");
            File.WriteAllText(Path.Combine(RepoPath, "README.md"), "test\n");
            RunGit(RepoPath, "add", ".");
            RunGit(RepoPath, "commit", "-q", "-m", "init");
        }

        public void CreateBranch(string name) => RunGit(RepoPath, "branch", name);

        public string ListWorktrees()
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            p.StartInfo.ArgumentList.Add("worktree");
            p.StartInfo.ArgumentList.Add("list");
            p.StartInfo.ArgumentList.Add("--porcelain");
            p.Start();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        private static void RunGit(string cwd, params string[] args)
        {
            using var p = new Process();
            p.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) p.StartInfo.ArgumentList.Add(a);
            p.Start();
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} failed: {p.StandardError.ReadToEnd()}");
            }
        }

        public void Dispose()
        {
            var parent = Directory.GetParent(RepoPath)!.FullName;
            try
            {
                // Best-effort: remove read-only bits that git sets on
                // pack files so Directory.Delete can complete on Windows.
                foreach (var f in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                }
                Directory.Delete(parent, recursive: true);
            }
            catch
            {
                // Leak the temp dir rather than fail the test on cleanup.
            }
        }
    }

    [Fact]
    public void ScriptFile_Exists()
    {
        File.Exists(ScriptPath).ShouldBeTrue(
            $"worktree-manager.ps1 must live at {ScriptPath}");
    }

    [Fact]
    public async Task SpawnWithBadBaseBranch_ReturnsWorktreeAddFailedButExitsZero()
    {
        if (!PwshAvailable) return;

        // Per polyphony-workflow-author convention: helper scripts exit
        // 0 and surface failures via `error_code` so the workflow's
        // catch-all route can fire without halting the conductor run.
        var (exitCode, stdout, _) = await RunScriptAsync(
            "-Operation spawn -WorkItemId 99999998 -BaseBranch nonexistent_branch_xyz");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeFalse();
        root.GetProperty("operation").GetString().ShouldBe("spawn");
        root.GetProperty("work_item_id").GetInt32().ShouldBe(99999998);
        root.GetProperty("error_code").GetString().ShouldBe("worktree_add_failed");
        root.GetProperty("error_message").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task TeardownNonexistentWorktree_IsIdempotentSuccess()
    {
        if (!PwshAvailable) return;

        // Teardown is idempotent: a worktree that does not exist is a
        // no-op success so apex-driver re-entry on resume cannot wedge
        // on a half-cleaned-up dispatch.
        var (exitCode, stdout, _) = await RunScriptAsync(
            "-Operation teardown -WorkItemId 99999999");

        exitCode.ShouldBe(0);

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeTrue();
        root.GetProperty("operation").GetString().ShouldBe("teardown");
        root.GetProperty("error_code").GetString().ShouldBe(string.Empty);
    }

    [Fact]
    public async Task EnvelopeAlwaysHasAllKeys()
    {
        if (!PwshAvailable) return;

        // Workflow templates reference every envelope field on the
        // catch-all route, so all keys must always be present.
        var inputs = new[]
        {
            "-Operation spawn -WorkItemId 1 -BaseBranch nonexistent_xyz",
            "-Operation teardown -WorkItemId 1",
        };

        var requiredKeys = new[]
        {
            "success", "operation", "work_item_id", "worktree_path",
            "branch", "error_code", "error_message",
        };

        foreach (var args in inputs)
        {
            var (exitCode, stdout, stderr) = await RunScriptAsync(args);
            exitCode.ShouldBe(0, $"args: {args} stderr: {stderr}");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            foreach (var key in requiredKeys)
            {
                root.TryGetProperty(key, out _).ShouldBeTrue($"{key} missing for {args}");
            }
        }
    }

    [Fact]
    public async Task BranchName_IsApexConventional()
    {
        if (!PwshAvailable) return;

        // Branch name is an apex-driver contract: feature-pr.yaml and
        // wave-integrator.ps1 both rely on the sdlc/apex/<id> pattern.
        var (_, stdout, _) = await RunScriptAsync(
            "-Operation spawn -WorkItemId 42 -BaseBranch nonexistent_xyz");

        using var doc = JsonDocument.Parse(stdout);
        doc.RootElement.GetProperty("branch").GetString().ShouldBe("sdlc/apex/42");
    }

    [Fact]
    public async Task SpawnWhenBranchExistsAndWorktreeAbsent_AttachesToExistingBranch()
    {
        if (!PwshAvailable) return;

        // Bug #10 (issue #177): a prior aborted run can leave
        // sdlc/apex/{id} as a local branch with no worktree directory.
        // The spawn must attach to the existing branch rather than
        // failing with "branch already exists". Resume semantics: any
        // in-flight commits on the branch are preserved.
        using var fixture = new TempGitRepo();
        fixture.CreateBranch("sdlc/apex/777");

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            $"-Operation spawn -WorkItemId 777 -BaseBranch main -WorktreeRoot \"{fixture.WorktreeRoot}\"",
            workingDirectory: fixture.RepoPath);

        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;

        root.GetProperty("success").GetBoolean().ShouldBeTrue($"stdout: {stdout}");
        root.GetProperty("branch").GetString().ShouldBe("sdlc/apex/777");
        root.GetProperty("error_code").GetString().ShouldBe(string.Empty);

        var listing = fixture.ListWorktrees();
        listing.ShouldContain("branch refs/heads/sdlc/apex/777");
    }

    [Fact]
    public async Task SpawnWhenBranchCheckedOutElsewhere_FailsWithBranchInUse()
    {
        if (!PwshAvailable) return;

        // Defensive guard: when sdlc/apex/{id} is the live checkout of
        // another worktree (e.g. a parallel concurrent run, or the
        // repo's primary working tree), we MUST NOT silently attach a
        // second worktree to the same branch — git refuses with a
        // confusing error and the surface is hard to reason about.
        // Fail fast with branch_in_use instead.
        using var fixture = new TempGitRepo();

        // Create a worktree A on sdlc/apex/888 first.
        var (codeA, stdoutA, stderrA) = await RunScriptAsync(
            $"-Operation spawn -WorkItemId 888 -BaseBranch main -WorktreeRoot \"{fixture.WorktreeRoot}\"",
            workingDirectory: fixture.RepoPath);
        codeA.ShouldBe(0, $"stderr: {stderrA}");
        using var docA = JsonDocument.Parse(stdoutA);
        docA.RootElement.GetProperty("success").GetBoolean().ShouldBeTrue();

        // Now blow away the dir of worktree A WITHOUT pruning the
        // worktree entry, then ask the script to spawn worktree B for
        // the same id. The branch is still "checked out" by A from
        // git's perspective; the script must refuse.
        var aPath = Path.Combine(fixture.WorktreeRoot, "item-888");
        // Don't actually delete — instead, just point WorktreeRoot
        // somewhere new and try to spawn id 888 again. Git will see
        // sdlc/apex/888 still checked out at worktree A and refuse.
        var altRoot = Path.Combine(Path.GetDirectoryName(fixture.WorktreeRoot)!, "alt");
        Directory.CreateDirectory(altRoot);

        var (codeB, stdoutB, stderrB) = await RunScriptAsync(
            $"-Operation spawn -WorkItemId 888 -BaseBranch main -WorktreeRoot \"{altRoot}\"",
            workingDirectory: fixture.RepoPath);

        codeB.ShouldBe(0, $"stderr: {stderrB}");

        using var docB = JsonDocument.Parse(stdoutB);
        var rootB = docB.RootElement;
        rootB.GetProperty("success").GetBoolean().ShouldBeFalse();
        rootB.GetProperty("error_code").GetString().ShouldBe("branch_in_use");
        rootB.GetProperty("error_message").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task SpawnFreshBranch_StillUsesCreateBranchPath()
    {
        if (!PwshAvailable) return;

        // Regression guard: when no local branch exists, spawn must
        // still take the `git worktree add -b` path so the new branch
        // is created from -BaseBranch rather than failing or silently
        // attaching to something stale.
        using var fixture = new TempGitRepo();

        var (exitCode, stdout, stderr) = await RunScriptAsync(
            $"-Operation spawn -WorkItemId 9001 -BaseBranch main -WorktreeRoot \"{fixture.WorktreeRoot}\"",
            workingDirectory: fixture.RepoPath);

        exitCode.ShouldBe(0, $"stderr: {stderr}");

        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        root.GetProperty("success").GetBoolean().ShouldBeTrue($"stdout: {stdout}");
        root.GetProperty("error_code").GetString().ShouldBe(string.Empty);

        var listing = fixture.ListWorktrees();
        listing.ShouldContain("branch refs/heads/sdlc/apex/9001");
    }
}
