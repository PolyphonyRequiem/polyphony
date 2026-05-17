using System.Diagnostics;
using System.Text.Json;
using Polyphony;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Move #2 Layer 2 — CLI dispatcher contract. ConsoleAppFramework v5 treats
/// an unknown first-token verb as <c>--help</c> (exit 0 + usage on stdout),
/// which conductor cannot distinguish from a silent success. The dispatcher
/// in <c>Program.cs</c> intercepts that case and emits a routing-style
/// envelope on stdout with <c>action == "error"</c> and a non-zero exit code.
/// These tests exec the actual built binary to exercise the top-level
/// statements (which are not unit-testable in-process).
/// </summary>
public sealed class CrossProcessLayer2DispatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly string DebugDll = Path.Combine(
        RepoRoot, "src", "Polyphony", "bin", "Debug", "net11.0", "polyphony.dll");

    private static bool BinaryExists => File.Exists(DebugDll);

    [Fact]
    public async Task UnknownVerb_ExitsNonZeroAndEmitsEnvelope()
    {
        if (!BinaryExists) return; // Skip when build artifact absent.

        var (exitCode, stdout, _) = await RunAsync("definitely-not-a-real-verb");

        exitCode.ShouldNotBe(0,
            $"Unknown verb must produce non-zero exit. stdout=<{stdout}>");

        var envelope = JsonSerializer.Deserialize(
            ExtractFirstJsonObject(stdout),
            PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Error.ShouldContain("definitely-not-a-real-verb");
    }

    [Fact]
    public async Task UnknownFlag_ExitsNonZeroAndEmitsEnvelope()
    {
        if (!BinaryExists) return;

        // `validate` is a known verb; `--definitely-not-a-flag` is not. CAF
        // should reject parse, our wrapper should emit an envelope.
        var (exitCode, stdout, _) = await RunAsync("validate", "1", "begin_planning", "--definitely-not-a-flag", "x");

        exitCode.ShouldNotBe(0,
            $"Unknown flag must produce non-zero exit. stdout=<{stdout}>");

        // Either the verb halted with a sentinel envelope, or Layer 2 wrapped
        // the CAF parse error. Both shapes parse to RequiredInputErrorResult.
        var envelope = JsonSerializer.Deserialize(
            ExtractFirstJsonObject(stdout),
            PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
    }

    [Fact]
    public async Task BareInvocation_ExitsZeroAndShowsHelp()
    {
        if (!BinaryExists) return;

        var (exitCode, stdout, _) = await RunAsync(/* no args */);

        exitCode.ShouldBe(0, $"Bare invocation must exit 0 (CAF prints usage). stdout=<{stdout}>");
        // Help output is plain text, not JSON.
        stdout.TrimStart().ShouldNotStartWith("{");
    }

    [Fact]
    public async Task HelpFlag_ExitsZeroAndShowsHelp()
    {
        if (!BinaryExists) return;

        var (exitCode, stdout, _) = await RunAsync("--help");

        exitCode.ShouldBe(0, $"--help must exit 0. stdout=<{stdout}>");
        stdout.TrimStart().ShouldNotStartWith("{");
    }

    /// <summary>
    /// Regression for the CAF-bool wiring bug class (PR #451 + AB#3211):
    /// every call to <c>polyphony pr merge-impl-pr --delete-branch false</c>
    /// used to crash at CAF parse with
    /// <c>"Argument 'false' is not recognized."</c> — the dispatcher
    /// returned exit 1 with an error envelope, the workflow node had no
    /// error route, and the silent failure cascaded into a
    /// <c>squash_coverage_mismatch_gate</c> downstream. This test pins the
    /// fix at the CAF dispatch boundary: <c>--delete-branch false</c> must
    /// be accepted as a value form. The verb body itself will still fail
    /// (no real git/gh/ado in this exec environment), but it must fail
    /// AFTER our <see cref="StringBoolArg"/> parse succeeded — the
    /// dispatcher must never reject the value.
    /// </summary>
    [Theory]
    [InlineData("pr", "merge-impl-pr", "--delete-branch", "false")]
    [InlineData("pr", "merge-impl-pr", "--delete-branch", "true")]
    [InlineData("pr", "merge-impl-ado", "--delete-branch", "false")]
    [InlineData("pr", "merge-impl-ado", "--delete-branch", "true")]
    public async Task DeleteBranchValueForm_NotRejectedAtCafParse(
        string area, string verb, string flag, string value)
    {
        if (!BinaryExists) return;

        // Pass enough required flags that we reach the StringBoolArg parse
        // step rather than halting on a missing required arg first; pass
        // garbage for the rest so the verb body itself fails after CAF
        // accepts our flag.
        var (exitCode, stdout, stderr) = await RunAsync(
            area, verb,
            "--root-id", "100",
            "--item-id", "200",
            "--mg-path", "core",
            "--organization", "o",
            "--project", "p",
            "--repository", "r",
            flag, value);

        // The KEY assertion: CAF did not reject the value. Old behavior
        // crashed with this exact string and a non-zero exit before any
        // verb body code ran.
        var combined = stdout + "\n" + stderr;
        var sentinel = $"Argument '{value}' is not recognized.";
        combined.Contains(sentinel, StringComparison.Ordinal).ShouldBeFalse(
            $"CAF rejected --{flag} {value} at parse — the string-bool shim regressed. exit={exitCode} stdout=<{stdout}> stderr=<{stderr}>");
    }

    /// <summary>
    /// Companion to <see cref="DeleteBranchValueForm_NotRejectedAtCafParse"/>:
    /// confirms the value-rejection error envelope round-trips correctly
    /// when the workflow passes an unparseable string (operator typo).
    /// </summary>
    [Fact]
    public async Task DeleteBranchGarbageValue_EmitsRoutingErrorEnvelope()
    {
        if (!BinaryExists) return;

        var (exitCode, stdout, _) = await RunAsync(
            "pr", "merge-impl-pr",
            "--root-id", "100",
            "--item-id", "200",
            "--mg-path", "core",
            "--delete-branch", "yes");

        exitCode.ShouldNotBe(0);
        var envelope = JsonSerializer.Deserialize(
            ExtractFirstJsonObject(stdout),
            PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr merge-impl-pr");
        envelope.Error.ShouldContain("--delete-branch");
        envelope.Error.ShouldContain("'true' or 'false'");
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        process.StartInfo.ArgumentList.Add("exec");
        process.StartInfo.ArgumentList.Add(DebugDll);
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static string ExtractFirstJsonObject(string text)
    {
        // Grab the first '{' .. matching '}' chunk so we tolerate any
        // CAF noise that may leak before the envelope. Conductor's parser
        // is similarly forgiving — it scans for the JSON object.
        var start = text.IndexOf('{');
        if (start < 0) return text;
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0) return text[start..(i + 1)];
            }
        }
        return text[start..];
    }
}
