using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ConsoleAppFramework;
using Polyphony.Annotations;
using Polyphony.Journal;
using Polyphony.Journal.Drift;
using Polyphony.Models;
using Twig.Domain.Interfaces;

namespace Polyphony.Commands;

/// <summary>
/// W13 (AB#3294): <c>polyphony reconcile --root R</c>. Surfaces the
/// drift report under the same <see cref="JournalDriftAnalyzer"/> the
/// reset pipeline uses, then optionally folds chosen findings back into
/// the current lineage so the next pass sees them as in-scope.
///
/// <para>Two side-effect flags, both opt-in and both require
/// <c>--execute</c> to mutate state:</para>
/// <list type="bullet">
///   <item>
///     <c>--accept-external</c> — for every
///     <see cref="DriftClassifications.ExternalMutation"/> finding,
///     synthesize a <c>reconcile_accept_external</c> journal row that
///     records the observed actual state under the current run id. The
///     existing <c>CurrentExpectedState</c> projection picks up the new
///     effect on the next run, so future drift treats the resource as
///     consistent.
///   </item>
///   <item>
///     <c>--adopt KIND:ID</c> — single-shot ownership transfer for an
///     orphan resource. Writes a <c>reconcile_adopt</c> entry under the
///     current run id with <see cref="ResourceIntent.EnsurePresent"/> +
///     <see cref="ResourceMutation.Updated"/>, so the
///     <c>OwnedResources</c> projection includes it on the next pass.
///   </item>
/// </list>
///
/// <para><c>--repair-external</c> from the original B3 sketch is
/// deliberately deferred — repairing externals means platform-side
/// rollback that needs its own policy surface.</para>
/// </summary>
[VerbGroup("")]
public sealed class ReconcileCommand(
    IJournalStore store,
    IWorkItemRepository repository,
    JournalDriftAnalyzer analyzer,
    RunContext runContext)
{
    private readonly IJournalStore _store = store;
    private readonly IWorkItemRepository _repository = repository;
    private readonly JournalDriftAnalyzer _analyzer = analyzer;
    private readonly RunContext _runContext = runContext;

    /// <summary>
    /// Report drift against a root, optionally folding findings back
    /// into the current lineage.
    /// </summary>
    /// <param name="root">Root work item ID.</param>
    /// <param name="acceptExternal">
    /// When true, every <c>external_mutation</c> finding will be (or
    /// would be, in dry-run) absorbed into the current lineage.
    /// </param>
    /// <param name="adopt">
    /// Optional <c>KIND:ID</c> selector identifying a single orphan
    /// resource to attach to the current lineage.
    /// </param>
    /// <param name="execute">
    /// When false (default), report planned actions without mutating
    /// the journal. <c>--accept-external</c> and <c>--adopt</c> are
    /// inert in dry-run mode.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    [Command("reconcile")]
    [VerbResult(typeof(ReconcileResult))]
    public async Task<int> Run(
        int root = RequiredInput.MissingInt,
        bool acceptExternal = false,
        string adopt = "",
        bool execute = false,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("reconcile",
            ("--root", root == RequiredInput.MissingInt)) is { } halt)
            return halt;

        if (root <= 0)
        {
            EmitError(root, "root must be positive");
            return ExitCodes.RoutingFailure;
        }

        string? adoptKind = null;
        string? adoptId = null;
        if (!string.IsNullOrWhiteSpace(adopt))
        {
            var separator = adopt.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || separator == adopt.Length - 1)
            {
                EmitError(root, $"--adopt must be of the form KIND:ID (got '{adopt}')");
                return ExitCodes.ConfigError;
            }
            adoptKind = adopt[..separator];
            adoptId = adopt[(separator + 1)..];
        }

        DriftResult drift;
        try
        {
            var item = await _repository.GetByIdAsync(root, ct).ConfigureAwait(false);
            if (item is null)
            {
                EmitError(root, $"Work item {root} not found");
                return ExitCodes.CacheError;
            }

            var entries = await _store.QueryAsync(new JournalQuery { RootId = root }, ct).ConfigureAwait(false);
            var analysis = await _analyzer.AnalyzeAsync(root, entries, ct).ConfigureAwait(false);
            drift = analysis.Result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            EmitError(root, ex.Message);
            return ExitCodes.CacheError;
        }

        var accepted = new List<ReconciledFinding>();
        var adopted = new List<ReconciledFinding>();

        if (acceptExternal)
        {
            foreach (var finding in drift.Findings)
            {
                if (!string.Equals(finding.Classification, DriftClassifications.ExternalMutation, StringComparison.Ordinal))
                    continue;
                accepted.Add(ToReconciled(finding, "reconcile_accept_external"));
            }
        }

        if (adoptKind is not null && adoptId is not null)
        {
            var match = drift.Findings.FirstOrDefault(f =>
                string.Equals(f.Kind, adoptKind, StringComparison.Ordinal) &&
                string.Equals(f.Id, adoptId, StringComparison.Ordinal));
            if (match is null)
            {
                EmitError(root, $"--adopt {adopt}: no drift finding matched (kind/id not in current drift report)");
                return ExitCodes.RoutingFailure;
            }
            adopted.Add(ToReconciled(match, "reconcile_adopt"));
        }

        if (execute)
        {
            try
            {
                foreach (var entry in accepted)
                    await RecordSynthesizedAsync(root, entry, ResourceMutation.Changed, ct).ConfigureAwait(false);
                foreach (var entry in adopted)
                    await RecordSynthesizedAsync(root, entry, ResourceMutation.NoChangedExternalAlreadyPresent, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                EmitResult(new ReconcileResult
                {
                    Root = root,
                    RunId = _runContext.RunId,
                    Executed = true,
                    AcceptExternal = acceptExternal,
                    AdoptTarget = string.IsNullOrWhiteSpace(adopt) ? null : adopt,
                    Drift = drift,
                    AcceptedExternal = accepted,
                    Adopted = adopted,
                    Success = false,
                    Error = $"Journal write failed: {ex.Message}",
                });
                return ExitCodes.RoutingFailure;
            }
        }

        EmitResult(new ReconcileResult
        {
            Root = root,
            RunId = _runContext.RunId,
            Executed = execute,
            AcceptExternal = acceptExternal,
            AdoptTarget = string.IsNullOrWhiteSpace(adopt) ? null : adopt,
            Drift = drift,
            AcceptedExternal = accepted,
            Adopted = adopted,
            Success = true,
        });
        return ExitCodes.Success;
    }

    private async Task RecordSynthesizedAsync(int root, ReconciledFinding finding, ResourceMutation mutation, CancellationToken ct)
    {
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var attributes = new JsonObject
        {
            ["target_state"] = finding.ActualState ?? finding.ExpectedState,
            ["reconcile_classification"] = finding.Classification,
        };
        var payload = new JsonObject
        {
            ["resource_kind"] = finding.Kind,
            ["resource_id"] = finding.Id,
            ["expected_state"] = finding.ExpectedState,
            ["actual_state"] = finding.ActualState,
            ["classification"] = finding.Classification,
            ["reconcile_action"] = finding.Action,
        };
        var entryId = await _store.RecordStartAsync(
            new JournalEntryStart
            {
                RunId = _runContext.RunId,
                RootId = root,
                Action = finding.Action,
                Target = $"{finding.Kind}:{finding.Id}",
                StartedAt = startedAt,
                PayloadJson = payload.ToJsonString(),
            }, ct).ConfigureAwait(false);

        var effect = new JournalResourceEffect
        {
            Kind = finding.Kind,
            Id = finding.Id,
            Intent = ResourceIntent.EnsurePresent,
            Mutation = mutation,
            PolyphonyOwned = true,
            Attributes = attributes,
        };
        await _store.RecordEndAsync(
            entryId,
            JournalOutcome.Success,
            errorCode: null,
            errorMessage: null,
            payloadJson: payload.ToJsonString(),
            effects: new[] { effect },
            ct).ConfigureAwait(false);
    }

    private static ReconciledFinding ToReconciled(DriftFinding finding, string action) => new()
    {
        Kind = finding.Kind,
        Id = finding.Id,
        Classification = finding.Classification,
        ExpectedState = finding.ExpectedState,
        ActualState = finding.ActualState,
        Action = action,
    };

    private void EmitError(int root, string message)
    {
        EmitResult(new ReconcileResult
        {
            Root = root,
            RunId = _runContext.RunId,
            Executed = false,
            AcceptExternal = false,
            AdoptTarget = null,
            Drift = new DriftResult
            {
                Status = "error",
                RootId = root,
                Findings = Array.Empty<DriftFinding>(),
                Summary = new DriftSummary { Consistent = 0, ExternalDelete = 0, ExternalMutation = 0, ExternalCreate = 0 },
                ResetTargets = Array.Empty<ResetTargetDescriptor>(),
            },
            AcceptedExternal = Array.Empty<ReconciledFinding>(),
            Adopted = Array.Empty<ReconciledFinding>(),
            Success = false,
            Error = message,
        });
    }

    private static void EmitResult(ReconcileResult result)
        => Console.WriteLine(JsonSerializer.Serialize(result, PolyphonyJsonContext.Default.ReconcileResult));
}
