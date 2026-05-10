using System;
using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

public sealed class ExternalToolTimeoutExceptionFormatTests
{
    [Fact]
    public void FormatErrorMessage_WithStderr_IncludesToolDescriptionAttemptsAndStderrTail()
    {
        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "view", "1", "--json", "state"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60),
            lastBufferedStdout: string.Empty,
            lastBufferedStderr: "* Connection timed out after 60001 ms\n* Closing connection",
            lastElapsed: TimeSpan.FromSeconds(60.2));

        var msg = ex.FormatErrorMessage("gh pr view");

        msg.ShouldContain("gh pr view timed out after 3 attempt(s) of 60s each");
        msg.ShouldContain("last attempt 60.2s");
        msg.ShouldContain("Last attempt stderr (tail):");
        msg.ShouldContain("Connection timed out after 60001 ms");
    }

    [Fact]
    public void FormatErrorMessage_NoStderr_HintsAtGhDebugApi()
    {
        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "list"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60),
            lastBufferedStdout: string.Empty,
            lastBufferedStderr: string.Empty,
            lastElapsed: TimeSpan.FromSeconds(60));

        var msg = ex.FormatErrorMessage("gh pr list");

        msg.ShouldContain("gh pr list timed out after 3 attempt(s) of 60s each");
        msg.ShouldContain("No stderr was captured before kill");
        msg.ShouldContain("GH_DEBUG=api");
    }

    [Fact]
    public void FormatErrorMessage_RedactsTokensInStderrTail()
    {
        var stderrWithToken = "Authorization: Bearer ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab\n* request failed";

        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "view"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60),
            lastBufferedStdout: string.Empty,
            lastBufferedStderr: stderrWithToken,
            lastElapsed: TimeSpan.FromSeconds(60));

        var msg = ex.FormatErrorMessage("gh pr view");

        msg.ShouldNotContain("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ab");
        msg.ShouldContain("[REDACTED");
    }

    [Fact]
    public void FormatErrorMessage_LongStderr_TailTruncatedToLimit()
    {
        var bigStderr = new string('x', ExternalToolTimeoutException.StderrTailLimitChars + 1000);

        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "view"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60),
            lastBufferedStdout: string.Empty,
            lastBufferedStderr: bigStderr,
            lastElapsed: TimeSpan.FromSeconds(60));

        var msg = ex.FormatErrorMessage("gh pr view");

        msg.ShouldContain("…");
        // Whole formatted message bounded — header + tail (4096) + a bit of preamble.
        msg.Length.ShouldBeLessThan(ExternalToolTimeoutException.StderrTailLimitChars + 500);
    }

    [Fact]
    public void FormatErrorMessage_ZeroElapsed_OmitsLastAttemptDuration()
    {
        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "view"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60));

        var msg = ex.FormatErrorMessage("gh pr view");

        msg.ShouldNotContain("last attempt");
    }

    /// <summary>
    /// Issue #209: when GhClient captures a hang diagnostic sidecar,
    /// the path must surface in the operator-facing message so the
    /// gate prompt can point at it. Verifies the wiring at the
    /// exception layer; GhClient hooks the capture call up at the
    /// throw site.
    /// </summary>
    [Fact]
    public void FormatErrorMessage_WithDiagnosticPath_IncludesSnapshotLine()
    {
        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "view"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60),
            lastBufferedStdout: string.Empty,
            lastBufferedStderr: string.Empty,
            lastElapsed: TimeSpan.FromSeconds(60),
            diagnosticFilePath: @"C:\Users\me\AppData\Local\Temp\polyphony\gh-hang-20260510T120000Z-pid1234.diag.json");

        ex.DiagnosticFilePath.ShouldEndWith(".diag.json");
        var msg = ex.FormatErrorMessage("gh pr view");
        msg.ShouldContain("Diagnostic snapshot:");
        msg.ShouldContain("gh-hang-20260510T120000Z-pid1234.diag.json");
    }

    [Fact]
    public void DiagnosticFilePath_DefaultsToEmpty_WhenOmitted()
    {
        var ex = new ExternalToolTimeoutException(
            executable: "gh",
            arguments: ["pr", "view"],
            attempts: 3,
            timeoutPerAttempt: TimeSpan.FromSeconds(60));

        ex.DiagnosticFilePath.ShouldBe(string.Empty);
        ex.FormatErrorMessage("gh pr view").ShouldNotContain("Diagnostic snapshot");
    }
}
