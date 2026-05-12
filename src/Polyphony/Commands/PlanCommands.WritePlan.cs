using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
///
/// <para>When <c>--children-json</c> is supplied, the verb ALSO writes a
/// canonicalized sidecar at <c>plans/plan-{item_id}.children.json</c>. The
/// sidecar is the durable handoff between the architect (who emits
/// <c>output.children</c> in workflow runtime context) and the seeder
/// (which may run in a later execution where that runtime context is
/// gone — see AB#3106 dogfood, 2026-05-12). The companion
/// <c>commit_and_push</c> step in <c>plan-level.yaml</c> stages both files
/// so the sidecar lives in the plan PR alongside the plan markdown.</para>
/// </summary>
public sealed partial class PlanCommands
{
    /// <summary>
    /// Write the architect's plan markdown to <c>plans/plan-{item_id}.md</c>,
    /// and (when <c>--children-json</c> is supplied) the architect's
    /// structured children list to <c>plans/plan-{item_id}.children.json</c>.
    /// </summary>
    /// <param name="itemId">Work-item id whose plan is being written (positive).</param>
    /// <param name="contentJson">JSON-encoded markdown string (e.g. <c>{{ architect.output.plan | tojson }}</c> from a workflow). Must decode to a non-null string.</param>
    /// <param name="childrenJson">Optional JSON array of architect-emitted child entries (e.g. <c>{{ architect.output.children | tojson }}</c>). When supplied — even as an empty array <c>[]</c> — the sidecar <c>plans/plan-{item_id}.children.json</c> is written (or refreshed) so the seeder can recover children-json on workflow re-entry. Must parse as a JSON array.</param>
    /// <param name="plansDir">Override the plans directory (default <c>plans</c> relative to cwd). Useful for tests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Always <see cref="ExitCodes.Success"/>; routing is via JSON payload.</returns>
    [Command("write-plan")]
    [VerbResult(typeof(PlanWritePlanResult))]
    public async Task<int> WritePlan(
        int itemId = RequiredInput.MissingInt,
        string contentJson = "",
        string plansDir = "plans",
        string childrenJson = "",
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

        // Pre-validate the sidecar payload BEFORE we touch the markdown file.
        // We refuse to leave the plan markdown updated and the sidecar
        // half-written *due to a parse/shape error* — but the writes
        // themselves are sequential (md then sidecar), so a process crash
        // between them can still leave the operator in a half-applied state.
        // Retrying `write-plan` converges (md becomes Unchanged, sidecar
        // gets written), so the non-atomic write is acceptable for this
        // dogfood unblock; harden later with temp-file + rename if needed.
        byte[]? sidecarBytes = null;
        var childrenSupplied = !string.IsNullOrEmpty(childrenJson);
        if (childrenSupplied)
        {
            try
            {
                var node = JsonNode.Parse(childrenJson);
                if (node is not JsonArray)
                {
                    // Reject anything that isn't a JsonArray, including
                    // explicit `null`. Without this guard `JsonNode.Parse("null")`
                    // returns null and we'd silently treat it as "skipped"
                    // which is misleading and would let a malformed
                    // architect output through.
                    var got = node is null ? "null" : node.GetType().Name;
                    EmitWriteError(itemId, plansDir,
                        $"--children-json must decode to a JSON array (got {got})");
                    return ExitCodes.Success;
                }

                // Canonicalize: re-serialize via the indented writer so the
                // committed file has stable, diff-friendly formatting and
                // doesn't echo whitespace quirks from the workflow's tojson
                // template. Indented form makes review easier in the plan PR.
                var canonical = node.ToJsonString(SidecarSerializerOptions);
                sidecarBytes = Encoding.UTF8.GetBytes(canonical);
            }
            catch (JsonException ex)
            {
                EmitWriteError(itemId, plansDir, $"--children-json is not valid JSON: {ex.Message}");
                return ExitCodes.Success;
            }
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

        var sidecarPath = string.Empty;
        var sidecarHash = string.Empty;
        var sidecarUnchanged = false;
        if (sidecarBytes is not null)
        {
            sidecarPath = Path.GetFullPath(Path.Combine(plansDir, $"plan-{itemId}.children.json"));
            sidecarHash = Convert.ToHexStringLower(SHA256.HashData(sidecarBytes));
            if (File.Exists(sidecarPath))
            {
                var existingSidecar = await File.ReadAllBytesAsync(sidecarPath, ct).ConfigureAwait(false);
                var existingSidecarHash = Convert.ToHexStringLower(SHA256.HashData(existingSidecar));
                if (existingSidecarHash == sidecarHash)
                {
                    sidecarUnchanged = true;
                }
            }

            if (!sidecarUnchanged)
            {
                await File.WriteAllBytesAsync(sidecarPath, sidecarBytes, ct).ConfigureAwait(false);
            }
        }

        var result = new PlanWritePlanResult
        {
            ItemId = itemId,
            Path = path,
            BytesWritten = bytes.LongLength,
            ContentSha256 = hash,
            Unchanged = unchanged,
            ChildrenPath = sidecarPath,
            ChildrenSha256 = sidecarHash,
            ChildrenUnchanged = sidecarUnchanged,
            ChildrenSkipped = !childrenSupplied,
            Error = null,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanWritePlanResult));
        return ExitCodes.Success;
    }

    private static readonly JsonSerializerOptions SidecarSerializerOptions = new()
    {
        WriteIndented = true,
    };

    private static void EmitWriteError(int itemId, string plansDir, string message)
    {
        var result = new PlanWritePlanResult
        {
            ItemId = itemId,
            Path = string.Empty,
            BytesWritten = 0,
            ContentSha256 = string.Empty,
            Unchanged = false,
            ChildrenPath = string.Empty,
            ChildrenSha256 = string.Empty,
            ChildrenUnchanged = false,
            ChildrenSkipped = true,
            Error = message,
        };
        Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.PlanWritePlanResult));
    }
}

