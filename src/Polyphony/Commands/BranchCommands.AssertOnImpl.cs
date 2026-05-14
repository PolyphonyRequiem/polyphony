using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Branching;
using Polyphony.Infrastructure.Processes;

namespace Polyphony.Commands;

public sealed partial class BranchCommands
{
    /// <summary>
    /// Assert that git HEAD in the current working directory is on the
    /// expected per-item impl branch (<c>impl/{root_id}-{item_id}</c>).
    /// Defends against AB#3210 — the per-task impl executor in
    /// <c>implement-merge-group.yaml</c> was observed to dispatch the
    /// coder agent against a HEAD that did not match the assigned task,
    /// silently routing the resulting commit onto a sibling task's impl
    /// branch. This verb is read-only: it inspects HEAD via
    /// <c>git branch --show-current</c> and emits a routable verdict.
    /// </summary>
    /// <param name="rootId">ADO work-item id of the run's apex (focus) item.</param>
    /// <param name="itemId">ADO work-item id of the task this assertion is gating.</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("assert-on-impl")]
    [VerbResult(typeof(BranchAssertOnImplResult))]
    public async Task<int> AssertOnImpl(
        int rootId = RequiredInput.MissingInt,
        int itemId = RequiredInput.MissingInt,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("branch assert-on-impl",
            ("--root-id", rootId == RequiredInput.MissingInt),
            ("--item-id", itemId == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (!RootId.TryParse(rootId, out var root))
        {
            EmitAssertError(rootId, itemId, $"rootId must be positive (got {rootId})");
            return ExitCodes.ConfigError;
        }

        if (!WorkItemId.TryParse(itemId, out var item))
        {
            EmitAssertError(rootId, itemId, $"itemId must be positive (got {itemId})");
            return ExitCodes.ConfigError;
        }

        var expected = BranchNameBuilder.Impl(root, item).Value;

        try
        {
            var actual = await git.GetCurrentBranchAsync(ct).ConfigureAwait(false);
            // GetCurrentBranchAsync returns null when git itself failed
            // (no repo, etc.) and empty when HEAD is detached. Treat both
            // as a mismatch with empty ActualBranch — the gate downstream
            // can present them identically.
            var normalized = string.IsNullOrEmpty(actual) ? string.Empty : actual;
            var matches = string.Equals(normalized, expected, StringComparison.Ordinal);

            EmitAssert(new BranchAssertOnImplResult
            {
                Action = matches ? "ok" : "mismatch",
                ExpectedBranch = expected,
                ActualBranch = normalized,
                RootId = rootId,
                ItemId = itemId,
            });
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            EmitAssertError(rootId, itemId, ex.Message, expected);
            return ExitCodes.RoutingFailure;
        }
    }

    private static void EmitAssert(BranchAssertOnImplResult result)
        => Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.BranchAssertOnImplResult));

    private static void EmitAssertError(int rootId, int itemId, string message, string expected = "")
    {
        EmitAssert(new BranchAssertOnImplResult
        {
            Action = "error",
            ExpectedBranch = expected,
            ActualBranch = string.Empty,
            RootId = rootId,
            ItemId = itemId,
            Error = message,
        });
    }
}
