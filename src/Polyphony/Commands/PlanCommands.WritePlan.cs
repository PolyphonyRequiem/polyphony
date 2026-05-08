using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Models;

namespace Polyphony.Commands;

/// <summary>
/// Phase 3 P7b foundation: write the architect's plan markdown to disk
/// (<c>plans/plan-{item_id}.md</c>) so it can be committed and reviewed
/// via a plan PR. Designed to be called from <c>plan-level.yaml</c> after
/// <c>branch ensure-plan</c> has switched to the plan branch.
///
/// The verb is intentionally pure I/O: no git operations, no manifest
/// reads, no PR interaction. Workflow steps before this verb own branch
/// state; workflow steps after this verb own commit + push.
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Write the architect's plan markdown to <c>plans/plan-{item_id}.md</c>.
    /// </summary>
    /// <param name="itemId">Work-item id whose plan is being written (positive).</param>
    /// <param name="contentJson">JSON-encoded markdown string (e.g. <c>{{ architect.output.plan | tojson }}</c> from a workflow). Must decode to a non-null string.</param>
    /// <param name="plansDir">Override the plans directory (default <c>plans</c> relative to cwd). Useful for tests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always <see cref="ExitCodes.Success"/>; routing is via JSON payload.</returns>
    [Command("write-plan")]
    [VerbResult(typeof(PlanWritePlanResult))]
    public async Task<int> WritePlan(
        int itemId = RequiredInput.MissingInt,
        string contentJson = "",
        string plansDir = "plans",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("plan write-plan",
            ("--item-id", itemId == RequiredInput.MissingInt),
            ("--content-json", string.IsNullOrEmpty(contentJson))) is { } halt)
            return halt;

        if (itemId <= 0)
        {
            EmitWriteError(itemId, plansDir, $"--item-id must be positive (got {itemId})");
            return ExitCodes.Success;
        }

        if (string.IsNullOrEmpty(contentJson))
        {
            EmitWriteError(itemId, plansDir, "--content-json must be provided");
            return ExitCodes.Success;
        }

        string content;
        try
        {
            using var doc = JsonDocument.Parse(contentJson);
            if (doc.RootElement.ValueKind != JsonValueKind.String)
            {
                EmitWriteError(itemId, plansDir, $"--content-json must decode to a JSON string (got {doc.RootElement.ValueKind})");
                return ExitCodes.Success;
            }
            content = doc.RootElement.GetString() ?? string.Empty;
        }
        catch (JsonException ex)
        {
            EmitWriteError(itemId, plansDir, $"--content-json is not valid JSON: {ex.Message}");
            return ExitCodes.Success;
        }

        var path = Path.GetFullPath(Path.Combine(plansDir, $"plan-{itemId}.md"));
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        bool unchanged = false;
        if (File.Exists(path))
        {
            var existing = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var existingHash = Convert.ToHexStringLower(SHA256.HashData(existing));
            if (existingHash == hash)
            {
                unchanged = true;
            }
        }

        if (!unchanged)
        {
            await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        }

        var result = new PlanWritePlanResult
        {
            ItemId = itemId,
            Path = path,
            BytesWritten = bytes.LongLength,
            ContentSha256 = hash,
            Unchanged = unchanged,
            Error = null,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanWritePlanResult));
        return ExitCodes.Success;
    }

    private static void EmitWriteError(int itemId, string plansDir, string message)
    {
        var result = new PlanWritePlanResult
        {
            ItemId = itemId,
            Path = string.Empty,
            BytesWritten = 0,
            ContentSha256 = string.Empty,
            Unchanged = false,
            Error = message,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanWritePlanResult));
    }
}
