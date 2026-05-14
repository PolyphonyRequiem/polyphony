BeforeAll {
    $script:LintScriptPath = Join-Path $PSScriptRoot 'lint-sync-after-mutation.ps1'

    # Build a synthetic command-source tree at $repo/$ScanDir so the lint
    # treats it as the "real" surface. Each test scenario writes one or
    # more `.cs` files into a fresh temp dir and invokes the lint pointing
    # at that dir.
    function New-CommandsSandbox {
        param([scriptblock] $Setup)
        $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("lint-sync-mut-" + [guid]::NewGuid().ToString('N').Substring(0,8))
        $commands = Join-Path $sandbox 'src/Polyphony/Commands'
        New-Item -ItemType Directory -Path $commands -Force | Out-Null
        Push-Location -LiteralPath $commands
        try {
            & $Setup
        } finally {
            Pop-Location
        }
        return $sandbox
    }

    function Invoke-Lint {
        param([string] $RepoRoot, [string] $Format = 'plain')
        $output = pwsh -NoProfile -File $script:LintScriptPath -RepoRoot $RepoRoot -Format $Format 2>&1
        return @{
            Output   = ($output | Out-String)
            ExitCode = $global:LASTEXITCODE
        }
    }

    function Write-CommandFile {
        param([string] $Name, [string] $Content)
        Set-Content -LiteralPath $Name -Value $Content -Encoding UTF8 -NoNewline
    }
}

Describe 'lint-sync-after-mutation.ps1' {

    Context 'PASS scenarios' {

        It 'PASSes when scan directory contains no mutations' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'NoMutation.cs' @'
namespace Polyphony.Commands;

public sealed class NoMutationCommands(ITwigClient twig)
{
    public async Task<int> ReadOnlyAsync(int id, CancellationToken ct)
    {
        await twig.SyncAsync(ct).ConfigureAwait(false);
        var item = await twig.ShowAsync(id, ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when SetStateAsync is followed by SyncAsync before return' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'GoodVerb.cs' @'
namespace Polyphony.Commands;

public sealed class GoodCommands(ITwigClient twig)
{
    public async Task<int> GoodVerbAsync(int id, CancellationToken ct)
    {
        await twig.SetActiveAsync(id, ct).ConfigureAwait(false);
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when PatchFieldsAsync is followed by SyncAsync before return' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'GoodPatch.cs' @'
namespace Polyphony.Commands;

public sealed class GoodPatchCommands(ITwigClient twig)
{
    public async Task<int> PatchAsync(int id, CancellationToken ct)
    {
        await twig.PatchFieldsAsync(id, new Dictionary<string, string> { ["X"] = "y" }, ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when batched-loop mutations are flushed by a post-loop count-guarded SyncAsync' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'BatchedLoop.cs' @'
namespace Polyphony.Commands;

public sealed class BatchedCommands(ITwigClient twig)
{
    public async Task<int> CloseScopeAsync(IList<Item> items, CancellationToken ct)
    {
        var closed = new List<int>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await twig.SetActiveAsync(item.Id, ct).ConfigureAwait(false);
                await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
                closed.Add(item.Id);
            }
            catch (Exception)
            {
                continue;
            }
        }
        if (closed.Count > 0)
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);
        }
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when mutation is in a try block and SyncAsync is in finally' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'TryFinally.cs' @'
namespace Polyphony.Commands;

public sealed class TryFinallyCommands(ITwigClient twig)
{
    public async Task<int> WithFinallyAsync(int id, CancellationToken ct)
    {
        try
        {
            await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
            DoStuff();
            return 0;
        }
        finally
        {
            await twig.SyncAsync(ct).ConfigureAwait(false);
        }
    }
    private void DoStuff() { }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes on multi-mutation, multi-sync pairs (each mutation flushed before its return)' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'TwoPaths.cs' @'
namespace Polyphony.Commands;

public sealed class TwoPathsCommands(ITwigClient twig)
{
    public async Task<int> ConditionalAsync(int id, bool early, CancellationToken ct)
    {
        await twig.SetStateAsync("A", ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        if (early) { return 1; }
        await twig.PatchFieldsAsync(id, new Dictionary<string, string> { ["X"] = "y" }, ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when this.twig receiver is used' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'ThisReceiver.cs' @'
namespace Polyphony.Commands;

public sealed class ThisReceiverCommands
{
    private readonly ITwigClient twig;
    public ThisReceiverCommands(ITwigClient twig) { this.twig = twig; }
    public async Task<int> DoAsync(int id, CancellationToken ct)
    {
        await this.twig.SetStateAsync("Done", ct).ConfigureAwait(false);
        await this.twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when a property accessor coexists with a mutation method (props are skipped)' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'WithProperty.cs' @'
namespace Polyphony.Commands;

public sealed class PropertyCommands(ITwigClient twig)
{
    public string Foo { get; set; } = "x";
    public int Bar => 42;

    public async Task<int> MutateAsync(int id, CancellationToken ct)
    {
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
                $r.Output | Should -Match 'PASS'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'FAIL scenarios' {

        It 'FAILs when SetStateAsync is followed by an unguarded return with no SyncAsync' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'BadVerb.cs' @'
namespace Polyphony.Commands;

public sealed class BadCommands(ITwigClient twig)
{
    public async Task<int> BadVerbAsync(int id, CancellationToken ct)
    {
        await twig.SetActiveAsync(id, ct).ConfigureAwait(false);
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'FAIL'
                $r.Output | Should -Match 'BadVerb\.cs'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'FAILs when PatchFieldsAsync is followed by an unguarded return with no SyncAsync' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'BadPatch.cs' @'
namespace Polyphony.Commands;

public sealed class BadPatchCommands(ITwigClient twig)
{
    public async Task<int> BadPatchAsync(int id, CancellationToken ct)
    {
        await twig.PatchFieldsAsync(id, new Dictionary<string, string> { ["X"] = "y" }, ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'FAIL'
                $r.Output | Should -Match 'BadPatch\.cs'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'FAILs when an early return precedes the SyncAsync' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'EarlyReturn.cs' @'
namespace Polyphony.Commands;

public sealed class EarlyReturnCommands(ITwigClient twig)
{
    public async Task<int> EarlyReturnAsync(int id, bool oops, CancellationToken ct)
    {
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
        if (oops) { return 1; }
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'EarlyReturn\.cs'
                $r.Output | Should -Match 'return at line'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'FAILs in github format with ::error annotations' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'GhBad.cs' @'
namespace Polyphony.Commands;

public sealed class GhBadCommands(ITwigClient twig)
{
    public async Task<int> BadAsync(int id, CancellationToken ct)
    {
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo -Format github
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match '::error file=src/Polyphony/Commands/GhBad\.cs'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Whitelist marker' {

        It 'PASSes when the mutation line carries the sync-after-mutation-ok marker' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'Whitelisted.cs' @'
namespace Polyphony.Commands;

public sealed class WhitelistedCommands(ITwigClient twig)
{
    public async Task<int> StagingHelperAsync(int id, CancellationToken ct)
    {
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false); // sync-after-mutation-ok: caller flushes
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when the method declaration line carries the sync-after-mutation-ok marker' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'WhitelistedDecl.cs' @'
namespace Polyphony.Commands;

public sealed class WhitelistedDeclCommands(ITwigClient twig)
{
    // sync-after-mutation-ok: internal helper, MutateAsync flushes
    private async Task StageOnlyAsync(int id, CancellationToken ct)
    {
        await twig.SetStateAsync("Done", ct).ConfigureAwait(false);
    }

    public async Task MutateAsync(int id, CancellationToken ct)
    {
        await StageOnlyAsync(id, ct);
        await twig.SyncAsync(ct).ConfigureAwait(false);
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Edge cases' {

        It 'PASSes when a mutation appears inside a string literal (sanitizer kills it)' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'StringLiteral.cs' @'
namespace Polyphony.Commands;

public sealed class StringLiteralCommands
{
    public string Help => "use SetStateAsync(id, state, ct) to mutate then SyncAsync";
    public int X => 42;
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes when a mutation-shaped token appears in a comment' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'CommentedOut.cs' @'
namespace Polyphony.Commands;

public sealed class CommentedOutCommands(ITwigClient twig)
{
    public async Task DoAsync(int id, CancellationToken ct)
    {
        // Historical note: this used to call twig.SetStateAsync(id, "Done", ct).
        await twig.SyncAsync(ct).ConfigureAwait(false);
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'EXITs 2 when scan directory does not exist' {
            $sandbox = Join-Path ([System.IO.Path]::GetTempPath()) ("lint-sync-mut-no-dir-" + [guid]::NewGuid().ToString('N').Substring(0,8))
            New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
            try {
                $r = Invoke-Lint -RepoRoot $sandbox
                $r.ExitCode | Should -Be 2
                $r.Output | Should -Match 'FATAL'
            } finally { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'PASSes on a partial-class file split (each file linted independently)' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'Split.cs' @'
namespace Polyphony.Commands;

public sealed partial class SplitCommands(ITwigClient twig);
'@
                Write-CommandFile 'Split.MutateA.cs' @'
namespace Polyphony.Commands;

public sealed partial class SplitCommands
{
    public async Task<int> MutateAAsync(int id, CancellationToken ct)
    {
        await twig.SetStateAsync("A", ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
                Write-CommandFile 'Split.MutateB.cs' @'
namespace Polyphony.Commands;

public sealed partial class SplitCommands
{
    public async Task<int> MutateBAsync(int id, CancellationToken ct)
    {
        await twig.PatchFieldsAsync(id, new Dictionary<string, string> { ["X"] = "y" }, ct).ConfigureAwait(false);
        await twig.SyncAsync(ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'FAILs each partial-class file independently when one is missing the flush' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'Split.cs' @'
namespace Polyphony.Commands;

public sealed partial class SplitCommands(ITwigClient twig);
'@
                Write-CommandFile 'Split.MutateA.cs' @'
namespace Polyphony.Commands;

public sealed partial class SplitCommands
{
    public async Task<int> MutateAAsync(int id, CancellationToken ct)
    {
        await twig.SetStateAsync("A", ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 1
                $r.Output | Should -Match 'Split\.MutateA\.cs'
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }

    Context 'Real-world parser robustness' {

        It 'handles primary constructors on the type (does not treat the class body as a method body)' {
            $repo = New-CommandsSandbox {
                Write-CommandFile 'PrimaryCtor.cs' @'
namespace Polyphony.Commands;

public sealed class PrimaryCtorCommands(
    ITwigClient twig,
    IWorkItemRepository repo)
{
    public async Task<int> ReadOnlyAsync(int id, CancellationToken ct)
    {
        var item = await repo.GetByIdAsync(id, ct).ConfigureAwait(false);
        return 0;
    }
}
'@
            }
            try {
                $r = Invoke-Lint -RepoRoot $repo
                $r.ExitCode | Should -Be 0
            } finally { Remove-Item -LiteralPath $repo -Recurse -Force -ErrorAction SilentlyContinue }
        }
    }
}
