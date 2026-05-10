using System.Text.Json;
using Polyphony.Infrastructure.Processes;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Infrastructure.Processes;

/// <summary>
/// Tests for <see cref="GhHangDiagnostics"/>. Exercises the real
/// filesystem under <c>%TEMP%/polyphony/</c> because the contract
/// of the helper is "drop a JSON sidecar on disk we can point an
/// operator at" — mocking the filesystem would test something else.
///
/// All tests clean up after themselves.
/// </summary>
public sealed class GhHangDiagnosticsTests
{
    [Fact]
    public void Capture_WritesJsonFileWithExpectedTopLevelKeys()
    {
        var path = GhHangDiagnostics.Capture(
            ghArgs: ["pr", "view", "https://github.com/o/r/pull/1", "--json", "state"],
            elapsed: TimeSpan.FromSeconds(60),
            attempts: 3,
            perAttemptTimeout: TimeSpan.FromSeconds(60));

        path.ShouldNotBeNull();
        try
        {
            File.Exists(path).ShouldBeTrue();
            var content = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            root.GetProperty("schema").GetInt32().ShouldBe(1);
            root.GetProperty("captured_at_utc").GetString().ShouldNotBeNullOrEmpty();
            root.GetProperty("polyphony_pid").GetInt32().ShouldBe(Environment.ProcessId);
            root.TryGetProperty("parent_pid", out _).ShouldBeTrue();

            var inv = root.GetProperty("gh_invocation");
            inv.GetProperty("attempts").GetInt32().ShouldBe(3);
            inv.GetProperty("per_attempt_timeout_seconds").GetDouble().ShouldBe(60d);
            inv.GetProperty("last_elapsed_seconds").GetDouble().ShouldBe(60d);
            inv.GetProperty("args").GetArrayLength().ShouldBe(5);

            root.TryGetProperty("gh_env_seen", out _).ShouldBeTrue();
            root.TryGetProperty("processes", out var procs).ShouldBeTrue();
            procs.ValueKind.ShouldBe(JsonValueKind.Array);
            root.TryGetProperty("process_handle_counts", out _).ShouldBeTrue();
        }
        finally
        {
            try { File.Delete(path!); } catch { }
        }
    }

    [Fact]
    public void Capture_FilenameMatchesGhHangPattern()
    {
        var path = GhHangDiagnostics.Capture(
            ghArgs: ["auth", "status"],
            elapsed: TimeSpan.Zero,
            attempts: 1,
            perAttemptTimeout: TimeSpan.FromSeconds(60));

        path.ShouldNotBeNull();
        try
        {
            var fileName = Path.GetFileName(path)!;
            fileName.ShouldStartWith("gh-hang-");
            fileName.ShouldEndWith($"-pid{Environment.ProcessId}.diag.json");
            Path.GetDirectoryName(path).ShouldEndWith("polyphony");
        }
        finally
        {
            try { File.Delete(path!); } catch { }
        }
    }

    /// <summary>
    /// Token-shaped env vars must be redacted: the diag file may be
    /// committed to a PR or shared with a third party. Verifies that
    /// if a fake GH_TOKEN is set during capture, only length + 6-char
    /// prefix appears in the JSON, never the full secret.
    /// </summary>
    [Fact]
    public void Capture_RedactsEnvVarsLookingLikeTokens()
    {
        const string fakeToken = "gho_FakeNeverRealForTestingOnlyXYZ1234567";
        Environment.SetEnvironmentVariable("GH_TEST_FAKE_TOKEN", fakeToken);
        try
        {
            var path = GhHangDiagnostics.Capture(
                ghArgs: ["pr", "view"],
                elapsed: TimeSpan.Zero,
                attempts: 1,
                perAttemptTimeout: TimeSpan.FromSeconds(60));

            path.ShouldNotBeNull();
            try
            {
                var content = File.ReadAllText(path!);
                content.ShouldNotContain(fakeToken);
                content.ShouldContain("len=");
                content.ShouldContain("prefix=gho_Fa");
            }
            finally
            {
                try { File.Delete(path!); } catch { }
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TEST_FAKE_TOKEN", null);
        }
    }
}
