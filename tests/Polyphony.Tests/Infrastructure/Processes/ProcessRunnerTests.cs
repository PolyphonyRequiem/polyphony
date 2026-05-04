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
}
