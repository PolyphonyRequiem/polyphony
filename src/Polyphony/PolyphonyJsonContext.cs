using System.Text.Json.Serialization;
using Polyphony.Configuration;
using Polyphony.Policy;

namespace Polyphony;

[JsonSerializable(typeof(RouteResult))]
[JsonSerializable(typeof(ValidateResult))]
[JsonSerializable(typeof(HierarchyResult))]
[JsonSerializable(typeof(HierarchyResult[]))]
[JsonSerializable(typeof(HealthResult))]
[JsonSerializable(typeof(HealthCheckResult))]
[JsonSerializable(typeof(ConfigValidationResult))]
[JsonSerializable(typeof(ConfigValidationDiagnostic[]))]
[JsonSerializable(typeof(PlanDepthGuardResult))]
[JsonSerializable(typeof(PlanNextChildResult))]
[JsonSerializable(typeof(PlannableChild))]
[JsonSerializable(typeof(PlanLoadTypeResult))]
[JsonSerializable(typeof(PlanReviewResult))]
[JsonSerializable(typeof(PolicyLoadResult))]
[JsonSerializable(typeof(PolicyValidateResult))]
[JsonSerializable(typeof(PolicyDomainSnapshot))]
[JsonSerializable(typeof(PolicyConcurrencySnapshot))]
[JsonSerializable(typeof(ResolvedRule))]
[JsonSerializable(typeof(BranchCheckDepsResult))]
[JsonSerializable(typeof(BlockingItem))]
[JsonSerializable(typeof(BranchCloseScopeResult))]
[JsonSerializable(typeof(ClosedItem))]
[JsonSerializable(typeof(FailedClosure))]
[JsonSerializable(typeof(BranchLoadTreeResult))]
[JsonSerializable(typeof(WorkTree))]
[JsonSerializable(typeof(WorkTreeIssue))]
[JsonSerializable(typeof(WorkTreeTask))]
[JsonSerializable(typeof(PullRequestGroup))]
[JsonSerializable(typeof(PgReconciliation))]
[JsonSerializable(typeof(BranchNextTaskResult))]
[JsonSerializable(typeof(BranchRouteResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class PolyphonyJsonContext : JsonSerializerContext;
