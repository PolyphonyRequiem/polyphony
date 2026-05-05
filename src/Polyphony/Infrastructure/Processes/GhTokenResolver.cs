using System.Diagnostics;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Resolves a GitHub auth token and sets <c>GH_TOKEN</c> process-wide so
/// all subsequent <c>gh</c> CLI invocations use the correct identity.
///
/// Resolution order (mirrors twig registry <c>resolve-gh-token.ps1</c>):
///   1. <c>GH_TOKEN</c> already set → no-op.
///   2. <c>GH_CONDUCTOR_USER</c> set → <c>gh auth token --user {value}</c>.
///      Fails loud if no token — never silently fall back to wrong identity.
///   3. Derive repo owner from <c>git remote get-url origin</c> →
///      <c>gh auth token --user {owner}</c>.
///   4. Fall back to <c>gh auth token</c> (active account).
///
/// Each <c>gh auth token</c> call uses a 10s timeout with 3 retries and
/// exponential backoff. <c>GH_PROMPT_DISABLED=1</c> is always set.
/// </summary>
public sealed class GhTokenResolver(IGitClient git)
{
    private const int MaxAttempts = 3;
    private const int TimeoutMs = 10_000;
    private const int BaseDelayMs = 1_000;
    private const int MaxDelayMs = 10_000;

    /// <summary>
    /// Resolve and set <c>GH_TOKEN</c>. Safe to call multiple times — returns
    /// immediately if already set. Non-throwing: logs to stderr on failure
    /// and lets callers degrade gracefully.
    /// </summary>
    public async Task ResolveAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GH_TOKEN")))
            return;

        // 1. Explicit user override
        var explicitUser = Environment.GetEnvironmentVariable("GH_CONDUCTOR_USER");
        var isExplicit = !string.IsNullOrEmpty(explicitUser);
        var user = explicitUser;

        // 2. Derive from repo remote owner
        if (string.IsNullOrEmpty(user))
        {
            try
            {
                var url = await git.GetRemoteUrlAsync("origin", ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(url))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        url, @"github\.com[:/]([^/]+)/");
                    if (match.Success)
                        user = match.Groups[1].Value;
                }
            }
            catch { /* non-fatal */ }
        }

        // 3. Try user-specific token
        string? token = null;
        if (!string.IsNullOrEmpty(user))
        {
            token = await InvokeGhAuthTokenAsync(["--user", user], ct).ConfigureAwait(false);
        }

        // Fail loud if explicit user was set but token can't be resolved
        if (token is null && isExplicit)
        {
            Console.Error.WriteLine(
                $"[GhTokenResolver] GH_CONDUCTOR_USER='{explicitUser}' but " +
                $"'gh auth token --user {explicitUser}' returned no token. " +
                $"Run 'gh auth login' or unset GH_CONDUCTOR_USER.");
            return;
        }

        // 4. Fall back to active account
        if (token is null)
        {
            token = await InvokeGhAuthTokenAsync([], ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(token))
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", token);
        }
    }

    private static async Task<string?> InvokeGhAuthTokenAsync(
        string[] extraArgs, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "gh",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("auth");
                psi.ArgumentList.Add("token");
                foreach (var a in extraArgs) psi.ArgumentList.Add(a);
                psi.Environment["GH_PROMPT_DISABLED"] = "1";

                using var proc = Process.Start(psi);
                if (proc is null) continue;

                var stdoutTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeoutMs);

                try
                {
                    await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // Timeout — kill and retry
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    continue;
                }

                if (proc.ExitCode == 0)
                {
                    var token = (await stdoutTask.ConfigureAwait(false)).Trim();
                    if (!string.IsNullOrEmpty(token))
                        return token;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* retry */ }

            if (attempt < MaxAttempts)
            {
                var delay = Math.Min(BaseDelayMs * (1 << (attempt - 1)), MaxDelayMs);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
        return null;
    }
}
