using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Tests for <see cref="ProcessRunner"/>. Uses cmd.exe (Windows) and the
/// dotnet binary (cross-platform) to exercise real process semantics —
/// no mocking — because the bug we're guarding against (deadlock on full
/// pipe buffers) only reproduces with a real OS pipe.
/// </summary>
public sealed class ProcessRunnerTests
{
    private static readonly bool IsWindows = OperatingSystem.IsWindows();

    [Fact]
    public async Task RunAsync_DotnetVersion_ReturnsZeroExitAndVersionString()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("dotnet", ["--version"]);

        result.ExitCode.ShouldBe(0);
        result.Succeeded.ShouldBeTrue();
        result.Stdout.Trim().ShouldNotBeNullOrEmpty();
        result.Stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunAsync_DotnetWithBogusFlag_ReturnsNonZeroExit()
    {
        var runner = new ProcessRunner();
        var result = await runner.RunAsync("dotnet", ["--definitely-not-a-flag"]);

        result.Succeeded.ShouldBeFalse();
        result.ExitCode.ShouldNotBe(0);
    }

    [Fact]
    public async Task RunAsync_LargeStdoutPayload_DoesNotDeadlock()
    {
        // The classic Process bug: child fills the OS pipe (~64KB on Windows,
        // ~64KB-1MB on Unix) writing stdout, blocks on the write, and the
        // parent blocks on WaitForExit. ProcessRunner must drain in parallel.
        // Use cmd.exe on Windows / sh on Unix to emit a script-driven blob.
        var runner = new ProcessRunner();

        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "for /L %i in (1,1,5000) do @echo line-%i" })
            : ("sh", new[] { "-c", "for i in $(seq 1 5000); do echo line-$i; done" });

        // Use a generous timeout — if the runner deadlocks, this would hang
        // indefinitely without one. xUnit's per-test timeout would catch it
        // eventually but a tight ct gives a clearer signal.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result = await runner.RunAsync(exe, args, cts.Token);

        result.ExitCode.ShouldBe(0);
        // 5000 lines * ~10 chars/line = ~50KB — past Windows pipe buffer.
        result.Stdout.Length.ShouldBeGreaterThan(40_000);
        result.Stdout.ShouldContain("line-1\n".Replace("\n", Environment.NewLine).Trim());
        result.Stdout.ShouldContain("line-5000");
    }

    [Fact]
    public async Task RunAsync_StderrCapturedSeparatelyFromStdout()
    {
        var runner = new ProcessRunner();

        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "echo OUT && echo ERR 1>&2" })
            : ("sh", new[] { "-c", "echo OUT; echo ERR >&2" });

        var result = await runner.RunAsync(exe, args);

        result.Stdout.Trim().ShouldBe("OUT");
        result.Stderr.Trim().ShouldBe("ERR");
    }

    [Fact]
    public async Task RunAsync_CancellationToken_KillsLongRunningProcess()
    {
        var runner = new ProcessRunner();

        var (exe, args) = IsWindows
            // ping with -n 30 takes ~30 seconds; we cancel after 200ms.
            ? ("cmd.exe", new[] { "/c", "ping 127.0.0.1 -n 30 > nul" })
            : ("sleep", new[] { "30" });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var task = runner.RunAsync(exe, args, cts.Token);

        // Should throw OperationCanceledException promptly, not after 30s.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Should.ThrowAsync<OperationCanceledException>(async () => await task);
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunAsync_TimeoutAfterEmittedOutput_ThrowsProcessCanceledWithBufferedOutput()
    {
        // Child writes some stdout AND stderr, then hangs. ProcessRunner
        // must drain whatever buffered before the kill and surface it on
        // the typed exception (instead of throwing a bare OCE that
        // discards the diagnostic).
        //
        // NB: we use try/catch instead of Should.ThrowAsync<ProcessCanceledException>.
        // Shouldly's async-throws assertion appears to lose OperationCanceledException
        // subtypes (likely intercepts TaskCanceledException specially in its
        // async wrapper) and reports the type as TaskCanceledException even
        // when the thrown object actually IS the PCE subclass. The manual
        // try/catch does not have this issue — both `is ProcessCanceledException`
        // and `is OperationCanceledException` evaluate true on the caught
        // instance.
        var runner = new ProcessRunner();

        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "echo HELLO-OUT & echo HELLO-ERR 1>&2 & ping 127.0.0.1 -n 30 > nul" })
            : ("sh", new[] { "-c", "echo HELLO-OUT; echo HELLO-ERR >&2; sleep 30" });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        ProcessCanceledException? captured = null;
        try
        {
            await runner.RunAsync(exe, args, cts.Token);
        }
        catch (ProcessCanceledException ex)
        {
            captured = ex;
        }

        captured.ShouldNotBeNull("expected ProcessCanceledException, got nothing thrown");
        captured.Executable.ShouldBe(exe);
        captured.Arguments.ShouldBe(args);
        captured.BufferedStdout.ShouldContain("HELLO-OUT");
        captured.BufferedStderr.ShouldContain("HELLO-ERR");
        captured.Elapsed.ShouldBeGreaterThan(TimeSpan.Zero);
        // Subclass relationship — existing OCE catches still match.
        ((object)captured).ShouldBeAssignableTo<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_EnvironmentOverride_AppliesToChildProcess()
    {
        var runner = new ProcessRunner();
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["POLYPHONY_TEST_VAR"] = "polyphony-test-value-42",
        };

        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "echo %POLYPHONY_TEST_VAR%" })
            : ("sh", new[] { "-c", "echo $POLYPHONY_TEST_VAR" });

        var result = await runner.RunAsync(exe, args, environment: env);

        result.Succeeded.ShouldBeTrue();
        result.Stdout.Trim().ShouldBe("polyphony-test-value-42");
    }

    [Fact]
    public async Task RunAsync_EnvironmentNullValue_RemovesInheritedVariable()
    {
        // Set the var on the parent, then clear it for the child via a null value.
        var runner = new ProcessRunner();
        const string varName = "POLYPHONY_TEST_INHERIT";
        Environment.SetEnvironmentVariable(varName, "PARENT-VALUE");
        try
        {
            var env = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [varName] = null,
            };

            var (exe, args) = IsWindows
                ? ("cmd.exe", new[] { "/c", $"echo [%{varName}%]" })
                : ("sh", new[] { "-c", $"echo \"[$" + varName + "]\"" });

            var result = await runner.RunAsync(exe, args, environment: env);

            result.Succeeded.ShouldBeTrue();
            // On Windows %UNDEFINED% expands to literally "%UNDEFINED%"; on Unix
            // $UNDEFINED expands to empty. Both are acceptable as long as the
            // PARENT-VALUE didn't leak through.
            result.Stdout.ShouldNotContain("PARENT-VALUE");
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public async Task RunAsync_WorkingDirectory_ChangesProcessCwd()
    {
        var runner = new ProcessRunner();

        // Resolve a known directory that exists on every machine.
        var tempDir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "cd" })
            : ("pwd", Array.Empty<string>());

        var result = await runner.RunAsync(exe, args, workingDirectory: tempDir);

        result.Succeeded.ShouldBeTrue();
        // Allow trailing slash differences and case differences on Windows.
        var actual = result.Stdout.Trim().TrimEnd(Path.DirectorySeparatorChar);
        actual.ShouldBe(tempDir, StringCompareShould.IgnoreCase);
    }

    /// <summary>
    /// Issue #209 regression: when <c>closeStdin: true</c> is requested
    /// and no stdin payload is supplied, the child must see EOF on read
    /// rather than block indefinitely on an inherited handle.
    ///
    /// Verifies by spawning a child that reads from stdin to completion
    /// and echoes the byte count it saw — closeStdin should produce 0
    /// bytes read, while the default (no closeStdin, no stdin payload)
    /// would inherit the parent's stdin and hang in a non-interactive
    /// runner without a TTY.
    /// </summary>
    [Fact]
    public async Task RunAsync_CloseStdinTrue_ChildSeesEofImmediately()
    {
        var runner = new ProcessRunner();

        // dotnet -e "Console.WriteLine(Console.In.ReadToEnd().Length);"
        // is too brittle across SDKs. Use cmd's "more" on Windows (reads
        // stdin to EOF and exits) and "cat" on POSIX (same behaviour).
        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "more" })
            : ("cat", Array.Empty<string>());

        // Bound the test by an outer timeout — if closeStdin is broken the
        // child hangs on stdin read and we'd hit this CTS, surfacing as
        // a clear test failure rather than a CI-blocking infinite hang.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await runner.RunAsync(exe, args, cts.Token, closeStdin: true);

        // Contract: the child sees EOF on stdin and exits promptly. The
        // exact stdout content varies by tool ('more' emits a trailing
        // newline; 'cat' emits nothing); what matters is that we got
        // here without the outer CTS firing.
        result.Succeeded.ShouldBeTrue();
        cts.IsCancellationRequested.ShouldBeFalse(
            "child should have exited well before the 15s outer timeout");
    }

    /// <summary>
    /// #116: when a Unicode body is piped to a child via stdin (e.g.
    /// <c>gh pr comment --body-file -</c> with arrows / emoji in the
    /// comment text), the bytes the child sees must be UTF-8, not the
    /// Windows ANSI default cp1252. The pre-fix runner left
    /// <see cref="ProcessStartInfo.StandardInputEncoding"/> at its
    /// default (<see cref="Console.InputEncoding"/>, cp1252 on
    /// Windows), which mapped unrepresentable chars to <c>?</c>
    /// (0x3F) or <c>SUB</c> (0x1A) — the visible mojibake on PR #113.
    ///
    /// Verify by piping a string containing <c>→</c> and <c>🔄</c>
    /// through a child that echoes stdin back on stdout (cmd /c more
    /// on Windows, cat on POSIX) and assert the round-trip preserves
    /// the original characters.
    /// </summary>
    [Fact]
    public async Task RunAsync_StdinWithUnicode_PreservesCharactersAsUtf8()
    {
        var runner = new ProcessRunner();

        var (exe, args) = IsWindows
            ? ("cmd.exe", new[] { "/c", "more" })
            : ("cat", Array.Empty<string>());

        const string payload = "Auto-rebased onto `parent` 🔄 abc → def";
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var result = await runner.RunAsync(exe, args, cts.Token, stdin: payload);

        result.Succeeded.ShouldBeTrue();
        // Both characters must survive the round-trip.
        result.Stdout.ShouldContain("🔄");
        result.Stdout.ShouldContain("→");
        // And the cp1252 mojibake sentinels must NOT appear.
        result.Stdout.ShouldNotContain("?\u001a");
    }
}
