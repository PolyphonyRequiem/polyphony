using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Configuration;
using Polyphony.Sdlc;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// Verbs in the <c>requirements</c> family. Today: <c>derive</c> only.
/// </summary>
[VerbGroup("requirements")]
public sealed class RequirementsCommands(
    IWorkItemRepository repository,
    ProcessConfig processConfig)
{
    /// <summary>
    /// Derive the requirement set + within-item edges for a work item.
    /// </summary>
    /// <param name="workItem">ADO work item ID to derive requirements for.</param>
    /// <param name="decomposable">REQUIRED. Whether the item is permitted to have
    /// children. There is no safe proxy for this; the planner is the authoritative
    /// source. Pass <c>true</c> for items that may be decomposed; <c>false</c> for leaves.</param>
    /// <param name="facetOrder">Comma-separated planner-declared ordering of the
    /// non-plannable facets (e.g. <c>actionable,implementable</c>). Required when
    /// the item carries BOTH actionable and implementable facets.</param>
    /// <param name="actionableExecutor">Required when the item is actionable.
    /// One of <c>polyphony</c> (evidence required) or <c>human</c> (no evidence).</param>
    /// <param name="ct">Cancellation token.</param>
    [Command("derive")]
    [VerbResult(typeof(RequirementsDeriveResult))]
    public async Task<int> Derive(
        int workItem = RequiredInput.MissingInt,
        bool decomposable = false,
        string facetOrder = "",
        string actionableExecutor = "",
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("requirements derive",
            ("--work-item", workItem == RequiredInput.MissingInt)) is { } halt)
            return halt;

        var item = await repository.GetByIdAsync(workItem, ct).ConfigureAwait(false);
        if (item is null)
        {
            EmitError(workItem, workItemType: "", facets: [], decomposable, facetOrder, actionableExecutor,
                $"Work item {workItem} not found.");
            return ExitCodes.CacheError;
        }

        var typeName = item.Type.Value ?? "";
        if (!processConfig.Types.TryGetValue(typeName, out var typeConfig))
        {
            EmitError(workItem, typeName, facets: [], decomposable, facetOrder, actionableExecutor,
                $"Type '{typeName}' not found in process-config.");
            return ExitCodes.ConfigError;
        }

        var facets = typeConfig.Facets;
        var orderList = ParseCommaList(facetOrder);
        var executor = string.IsNullOrEmpty(actionableExecutor) ? null : actionableExecutor;

        var derivation = RequirementSetDeriver.Derive(
            facets,
            decomposable,
            orderList,
            executor);

        var inputs = new RequirementsInputProvenance
        {
            // `decomposable` is always explicit for now (it's a required flag).
            Decomposable = RequirementsInputProvenance.Explicit,
            // facet_order is explicit when supplied; "not_applicable" otherwise.
            FacetOrder = orderList is null
                ? RequirementsInputProvenance.NotApplicable
                : RequirementsInputProvenance.Explicit,
            // actionable_executor is explicit when supplied; "not_applicable" otherwise.
            ActionableExecutor = executor is null
                ? RequirementsInputProvenance.NotApplicable
                : RequirementsInputProvenance.Explicit,
        };

        var result = new RequirementsDeriveResult
        {
            WorkItemId = workItem,
            WorkItemType = typeName,
            Facets = facets,
            Decomposable = decomposable,
            FacetOrder = orderList,
            ActionableExecutor = executor,
            RequirementSet = derivation.Set,
            Errors = derivation.Errors,
            Warnings = derivation.Warnings,
            Inputs = inputs,
        };

        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.RequirementsDeriveResult));

        return derivation.IsValid ? ExitCodes.Success : ExitCodes.ConfigError;
    }

    private static IReadOnlyList<string>? ParseCommaList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? null : parts;
    }

    private static void EmitError(
        int workItemId,
        string workItemType,
        IReadOnlyList<string> facets,
        bool decomposable,
        string facetOrder,
        string actionableExecutor,
        string message)
    {
        var inputs = new RequirementsInputProvenance
        {
            Decomposable = RequirementsInputProvenance.Explicit,
            FacetOrder = string.IsNullOrEmpty(facetOrder)
                ? RequirementsInputProvenance.NotApplicable
                : RequirementsInputProvenance.Explicit,
            ActionableExecutor = string.IsNullOrEmpty(actionableExecutor)
                ? RequirementsInputProvenance.NotApplicable
                : RequirementsInputProvenance.Explicit,
        };

        var result = new RequirementsDeriveResult
        {
            WorkItemId = workItemId,
            WorkItemType = workItemType,
            Facets = facets,
            Decomposable = decomposable,
            FacetOrder = string.IsNullOrEmpty(facetOrder) ? null : ParseCommaList(facetOrder),
            ActionableExecutor = string.IsNullOrEmpty(actionableExecutor) ? null : actionableExecutor,
            RequirementSet = null,
            Errors = [message],
            Warnings = [],
            Inputs = inputs,
            Error = message,
        };
        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.RequirementsDeriveResult));
    }
}
