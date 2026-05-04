using System.Text.Json;
using System.Text.Json.Nodes;

namespace Polyphony.Infrastructure.Processes;

/// <summary>
/// Default <see cref="ITwigClient"/> backed by <see cref="IProcessRunner"/>.
/// Spawns the <c>twig</c> binary from PATH on every call.
/// </summary>
public sealed class TwigClient(IProcessRunner runner) : ITwigClient
{
    private const string Exe = "twig";

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await runner.RunAsync(Exe, ["--version"], ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return null;
            }
            return result.Stdout.Trim();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Binary not found on PATH.
            return null;
        }
    }

    public async Task SyncAsync(CancellationToken ct = default)
    {
        var result = await runner.RunAsync(Exe, ["sync", "--output", "json"], ct).ConfigureAwait(false);
        ThrowIfFailed(["sync", "--output", "json"], result);
    }

    public async Task<JsonNode?> ShowAsync(int workItemId, CancellationToken ct = default)
    {
        string[] args = ["show", workItemId.ToString(), "--output", "json"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }
        return ParseJson(result.Stdout);
    }

    public async Task<JsonNode?> ShowTreeAsync(int workItemId, CancellationToken ct = default)
    {
        string[] args = ["show", workItemId.ToString(), "--tree", "--output", "json"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }
        return ParseJson(result.Stdout);
    }

    public async Task<JsonNode?> TreeAsync(int depth, CancellationToken ct = default)
    {
        string[] args = ["tree", "--depth", depth.ToString(), "--output", "json"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }
        return ParseJson(result.Stdout);
    }

    public async Task SetActiveAsync(int workItemId, CancellationToken ct = default)
    {
        string[] args = ["set", workItemId.ToString(), "--output", "json"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        ThrowIfFailed(args, result);
    }

    public async Task SetStateAsync(string stateName, CancellationToken ct = default)
    {
        string[] args = ["state", stateName, "--output", "json"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        ThrowIfFailed(args, result);
    }

    public async Task PatchFieldsAsync(int workItemId, IReadOnlyDictionary<string, string> fields, CancellationToken ct = default)
    {
        var fieldsJson = SerializeFields(fields);
        string[] args = ["patch", "--id", workItemId.ToString(), "--json", fieldsJson];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        ThrowIfFailed(args, result);
    }

    public async Task<JsonNode> CreateChildAsync(int parentId, string type, string title, string description, CancellationToken ct = default)
    {
        string[] args =
        [
            "new",
            "--type", type,
            "--title", title,
            "--description", description,
            "--parent", parentId.ToString(),
            "-o", "json",
        ];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        ThrowIfFailed(args, result);
        var node = ParseJson(result.Stdout)
            ?? throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, "twig new returned no JSON");
        return node;
    }

    public async Task<string?> GetConfigValueAsync(string key, CancellationToken ct = default)
    {
        string[] args = ["config", key, "--output", "json"];
        var result = await runner.RunAsync(Exe, args, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return null;
        }
        var node = ParseJson(result.Stdout);
        var info = node?["info"];
        return info?.GetValue<string>();
    }

    private static JsonNode? ParseJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SerializeFields(IReadOnlyDictionary<string, string> fields)
    {
        // PolyphonyJsonContext registers Dictionary<string,string>. Materialize
        // into a concrete dictionary so the source-gen path applies cleanly
        // (avoids reflection on the IReadOnlyDictionary interface, which is
        // not registered).
        var concrete = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        return JsonSerializer.Serialize(concrete, PolyphonyJsonContext.Default.DictionaryStringString);
    }

    private static void ThrowIfFailed(IReadOnlyList<string> args, ProcessResult result)
    {
        if (!result.Succeeded)
        {
            throw new ExternalToolException(Exe, args, result.ExitCode, result.Stdout, result.Stderr);
        }
    }
}
