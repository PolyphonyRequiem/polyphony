using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Polyphony.Configuration;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Infrastructure.Research;

/// <summary>
/// <see cref="IResearchStorage"/> backed by the GitHub Contents API via
/// <c>gh api</c>. Uses the configured <see cref="ResearchConfig"/> for
/// repository, branch, and base-path, and either the platform-router's
/// GH_TOKEN or an explicit per-config auth override (scoped to the child
/// process — never mutates the parent environment).
/// </summary>
public sealed class GitHubResearchStorage : IResearchStorage
{
    private readonly ResearchConfig _config;
    private readonly IProcessRunner _runner;
    private readonly GhClientPolicy _policy;

    /// <summary>
    /// Environment variables applied to every <c>gh api</c> subprocess.
    /// Same baseline as <see cref="GhClient"/>: disable prompts and colors.
    /// The auth-override token (if any) is layered on top at call time.
    /// </summary>
    private readonly Dictionary<string, string?> _baseEnvironment;

    public GitHubResearchStorage(
        ResearchConfig config,
        IProcessRunner runner,
        GhClientPolicy? policy = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _policy = policy ?? GhClientPolicy.Default;

        _baseEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GH_PROMPT_DISABLED"] = "1",
            ["NO_COLOR"] = "1",
        };

        // Scope the auth override to child-process env only.
        if (config.Auth?.TokenEnvVar is { } envVar
            && !string.IsNullOrWhiteSpace(envVar))
        {
            var token = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(token))
            {
                _baseEnvironment["GH_TOKEN"] = token;
            }
        }
    }

    public async Task<string?> ReadAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var repoPath = CombinePath(_config.BasePath, path);

        var endpoint = $"/repos/{_config.Repository}/contents/{repoPath}?ref={_config.Branch}";
        string[] args = ["api", endpoint, "--jq", ".content"];

        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // 404 = file not found → return null
            if (result.Stderr.Contains("404", StringComparison.OrdinalIgnoreCase)
                || result.Stderr.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            throw new ExternalToolException("gh", args, result.ExitCode, result.Stdout, result.Stderr);
        }

        var base64 = result.Stdout.Trim().Replace("\n", "").Replace("\r", "");
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    public async Task WriteAsync(string path, string content, string commitMessage, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(commitMessage);

        var repoPath = CombinePath(_config.BasePath, path);

        // Read-before-write: get the current SHA if the file exists.
        var sha = await GetFileShaAsync(repoPath, ct).ConfigureAwait(false);

        var payload = new JsonObject
        {
            ["message"] = commitMessage,
            ["content"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(content)),
            ["branch"] = _config.Branch,
        };

        if (sha is not null)
        {
            payload["sha"] = sha;
        }

        var body = payload.ToJsonString();

        string[] args =
        [
            "api",
            $"/repos/{_config.Repository}/contents/{repoPath}",
            "--method", "PUT",
            "--input", "-",
        ];

        var result = await RunWithRetryAsync(args, ct, stdin: body).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new ExternalToolException("gh", args, result.ExitCode, result.Stdout, result.Stderr);
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(string directoryPath, CancellationToken ct = default)
    {
        var repoPath = string.IsNullOrEmpty(directoryPath)
            ? _config.BasePath
            : CombinePath(_config.BasePath, directoryPath);

        // When repoPath is empty, list the repo root.
        var pathSegment = string.IsNullOrEmpty(repoPath) ? "" : $"/{repoPath}";

        var endpoint = $"/repos/{_config.Repository}/contents{pathSegment}?ref={_config.Branch}";
        string[] args = ["api", endpoint];

        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            if (result.Stderr.Contains("404", StringComparison.OrdinalIgnoreCase)
                || result.Stderr.Contains("Not Found", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            throw new ExternalToolException("gh", args, result.ExitCode, result.Stdout, result.Stderr);
        }

        return ParseDirectoryListing(result.Stdout, _config.BasePath);
    }

    private async Task<string?> GetFileShaAsync(string repoPath, CancellationToken ct)
    {
        var endpoint = $"/repos/{_config.Repository}/contents/{repoPath}?ref={_config.Branch}";
        string[] args = ["api", endpoint, "--jq", ".sha"];

        var result = await RunWithRetryAsync(args, ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return null; // File doesn't exist yet — create case.
        }

        var sha = result.Stdout.Trim();
        return string.IsNullOrEmpty(sha) ? null : sha;
    }

    private async Task<ProcessResult> RunWithRetryAsync(
        IReadOnlyList<string> args,
        CancellationToken ct,
        string? stdin = null)
    {
        ProcessResult? lastResult = null;

        for (int attempt = 1; attempt <= _policy.MaxAttempts; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_policy.PerAttemptTimeout);

            try
            {
                lastResult = await _runner.RunAsync(
                    "gh", args, timeoutCts.Token,
                    stdin: stdin,
                    environment: _baseEnvironment,
                    closeStdin: stdin is null).ConfigureAwait(false);

                return lastResult;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Caller cancelled — propagate immediately.
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-attempt timeout — retry if attempts remain.
                if (attempt >= _policy.MaxAttempts)
                {
                    throw new ExternalToolTimeoutException(
                        "gh", args,
                        _policy.MaxAttempts,
                        _policy.PerAttemptTimeout);
                }
            }

            if (attempt < _policy.MaxAttempts)
            {
                var delay = TimeSpan.FromTicks(_policy.InitialBackoff.Ticks * (1L << (attempt - 1)));
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }

        // Should never reach here, but satisfy the compiler.
        throw new InvalidOperationException("Retry loop exited without returning or throwing.");
    }

    private static IReadOnlyList<string> ParseDirectoryListing(string json, string basePath)
    {
        var files = new List<string>();

        try
        {
            var items = JsonSerializer.Deserialize<JsonArray>(json);
            if (items is null) return files;

            foreach (var item in items)
            {
                if (item is null) continue;
                var itemPath = item["path"]?.GetValue<string>();
                if (itemPath is null) continue;

                // Return paths relative to base_path.
                if (!string.IsNullOrEmpty(basePath) && itemPath.StartsWith(basePath, StringComparison.Ordinal))
                {
                    itemPath = itemPath[basePath.Length..].TrimStart('/');
                }

                files.Add(itemPath);
            }
        }
        catch (JsonException)
        {
            // Malformed response — return empty.
        }

        return files;
    }

    private static string CombinePath(string basePath, string relativePath)
    {
        if (string.IsNullOrEmpty(basePath))
            return relativePath.TrimStart('/');

        return $"{basePath.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }
}
