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
