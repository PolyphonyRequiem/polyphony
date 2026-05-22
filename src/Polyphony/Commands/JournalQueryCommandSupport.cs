using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using Polyphony.Journal;
using Polyphony.Journal.Observers;
using Polyphony.Journal.Projections;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;

namespace Polyphony.Commands;

internal static class JournalQueryCommandSupport
{
    private static readonly IReadOnlyDictionary<string, string> ResourceKinds = typeof(ResourceKind)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(string) && field.IsLiteral)
        .Select(field => (string?)field.GetRawConstantValue())
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Cast<string>()
        .ToDictionary(value => value, value => value, StringComparer.OrdinalIgnoreCase);

    internal static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static bool TryNormalizeKind(string? value, out string? kind, out string? error)
    {
        kind = null;
        error = null;

        var normalized = Normalize(value);
        if (normalized is null)
        {
            return true;
        }

        if (!ResourceKinds.TryGetValue(normalized, out kind))
        {
            error = $"kind must be one of: {string.Join(", ", ResourceKinds.Values.OrderBy(value => value, StringComparer.Ordinal))}";
            return false;
        }

        return true;
    }

    internal static bool TryParseTimestampFilter(string? value, string flag, out long? timestamp, out string? error)
    {
        timestamp = null;
        error = null;

        var normalized = Normalize(value);
        if (normalized is null)
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed))
        {
            error = $"{flag} must be a valid ISO-8601 timestamp";
            return false;
        }

        timestamp = parsed.ToUnixTimeMilliseconds();
        return true;
    }

    internal static async Task<RootLookupResult> LookupRootAsync(IServiceProvider services, int root, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        try
        {
            if (services.GetService(typeof(IWorkItemRepository)) is not IWorkItemRepository repository)
            {
                return new RootLookupResult { RepositoryAvailable = false, Exists = false };
            }

            var item = await repository.GetByIdAsync(root, ct).ConfigureAwait(false);
            return new RootLookupResult
            {
                RepositoryAvailable = true,
                Exists = item is not null,
            };
        }
        catch (WorkspaceNotFoundException ex)
        {
            return new RootLookupResult
            {
                RepositoryAvailable = false,
                Exists = false,
                Error = ex.Message,
            };
        }
    }

    internal static string DescribeExpectedState(ProjectedResourceState expected)
    {
        if (expected.Intent == ResourceIntent.EnsureAbsent)
        {
            return "absent";
        }

        var namedState = ResourceObserverSupport.GetStringAttribute(expected.Attributes, "target_state")
            ?? ResourceObserverSupport.GetStringAttribute(expected.Attributes, "state");
        if (!string.IsNullOrWhiteSpace(namedState))
        {
            return namedState;
        }

        var sha = ResourceObserverSupport.GetBranchExpectedSha(expected.Attributes);
        if (!string.IsNullOrWhiteSpace(sha))
        {
            return $"present@{sha}";
        }

        return expected.Intent switch
        {
            ResourceIntent.EnsurePresent => "present",
            ResourceIntent.AdvancePointer => "advanced",
            ResourceIntent.SetState => "updated",
            ResourceIntent.UpdateMetadata => "metadata_updated",
            ResourceIntent.Attach => "attached",
            ResourceIntent.Detach => "detached",
            ResourceIntent.Observe => "observed",
            _ => expected.Intent.ToString(),
        };
    }

    internal static JournalHasMatch ToHasMatch(JournalEntry entry)
        => new()
        {
            EntryId = entry.Id,
            Action = entry.Action,
            Target = entry.Target,
            RunId = entry.RunId,
            StartedAt = entry.StartedAt,
            RootId = entry.RootId,
            WorkItemId = entry.WorkItemId,
            FinishedAt = entry.FinishedAt,
            Outcome = entry.Outcome,
        };

    internal static JournalQueryEffect ToQueryEffect(ProjectedResourceState resource)
        => new()
        {
            Kind = resource.Kind,
            Id = resource.Id,
            ExpectedState = DescribeExpectedState(resource),
            Action = resource.Action,
            StartedAt = resource.StartedAt,
            Intent = resource.Intent,
            Mutation = resource.Mutation,
            PolyphonyOwned = resource.PolyphonyOwned,
            EntryId = resource.EntryId,
            Platform = resource.Platform,
            ParentId = resource.ParentId,
            Attributes = resource.Attributes?.DeepClone().AsObject(),
        };

    internal static JournalOwnedResource ToOwnedResource(ProjectedResourceState resource)
        => new()
        {
            Kind = resource.Kind,
            Id = resource.Id,
            ExpectedState = DescribeExpectedState(resource),
        };

    internal static bool MatchesPrimaryTarget(JournalEntry entry, string target)
        => entry.Effects.Any(effect => string.Equals(effect.Id, target, StringComparison.Ordinal));

    internal static bool MatchesState(ProjectedResourceState resource, string? state)
    {
        var normalized = Normalize(state);
        return normalized is null
            || string.Equals(DescribeExpectedState(resource), normalized, StringComparison.OrdinalIgnoreCase);
    }

    internal static void EmitRootNotFound(int root)
        => Console.WriteLine($$"""{"error":"Work item {{root}} not found","work_item_id":{{root}}}""");

    internal static void EmitError(string message)
        => Console.WriteLine($$"""{"error":"{{EscapeJsonString(message)}}"}""");

    private static string EscapeJsonString(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
}

internal sealed record RootLookupResult
{
    public required bool RepositoryAvailable { get; init; }
    public required bool Exists { get; init; }
    public string? Error { get; init; }
}
