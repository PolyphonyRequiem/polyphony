using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Polyphony.Journal;

namespace Polyphony.Commands;

internal static class JournalCommandSupport
{
    internal static RunContext ResolveRunContext(RunContext? runContext)
        => runContext ?? new RunContext();

    internal static JournaledActionDecorator ResolveDecorator(JournaledActionDecorator? decorator)
        => decorator ?? new JournaledActionDecorator(new NullJournalStore());

    internal static readonly IJournalStore NullStore = new NullJournalStore();

    internal static JournaledActionInvocation CreateInvocation(
        RunContext runContext,
        string action,
        string target,
        int? rootId = null,
        int? workItemId = null,
        string? payloadJson = null)
        => new()
        {
            RunId = runContext.RunId,
            RootId = rootId,
            WorkItemId = workItemId,
            Action = action,
            Target = target,
            PayloadJson = payloadJson,
        };

    internal static JournalOutcome SelectOutcome(int exitCode, bool succeeded, bool wasMutated)
    {
        if (exitCode != ExitCodes.Success || !succeeded)
        {
            return JournalOutcome.Failure;
        }

        return wasMutated ? JournalOutcome.Success : JournalOutcome.NoOp;
    }

    internal static string? SerializePayload<TPayload>(TPayload? payload, JsonTypeInfo<TPayload> jsonTypeInfo)
        where TPayload : class
        => payload is null ? null : JsonSerializer.Serialize(payload, jsonTypeInfo);

    internal static async Task<(int ExitCode, TResult? Result, string Output)> CaptureResultAsync<TResult>(
        Func<CancellationToken, Task<int>> action,
        JsonTypeInfo<TResult> resultTypeInfo,
        CancellationToken ct)
        where TResult : class
    {
        var writer = new StringWriter();
        var originalOut = Console.Out;
        int exitCode;
        try
        {
            Console.SetOut(writer);
            exitCode = await action(ct).ConfigureAwait(false);
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        var output = writer.ToString();
        TResult? result = null;
        try
        {
            result = JsonSerializer.Deserialize(output.Trim(), resultTypeInfo);
        }
        catch (JsonException)
        {
        }

        return (exitCode, result, output);
    }

    internal static async Task<int> RunWithCapturedResultAsync<TResult, TPayload>(
        JournaledActionDecorator decorator,
        RunContext runContext,
        string action,
        string target,
        Func<CancellationToken, Task<int>> body,
        JsonTypeInfo<TResult> resultTypeInfo,
        Func<int, TResult?, TPayload> payloadFactory,
        JsonTypeInfo<TPayload> payloadTypeInfo,
        Func<TPayload, bool> succeededSelector,
        Func<TPayload, bool> mutatedSelector,
        Func<TPayload, IReadOnlyList<JournalResourceEffect>> effectsSelector,
        CancellationToken ct,
        int? rootId = null,
        int? workItemId = null)
        where TResult : class
        where TPayload : class
    {
        TPayload? payload = null;

        return await decorator.RunWithAsync(
            CreateInvocation(runContext, action, target, rootId, workItemId),
            async innerCt =>
            {
                var (exitCode, result, output) = await CaptureResultAsync(body, resultTypeInfo, innerCt).ConfigureAwait(false);
                payload = payloadFactory(exitCode, result);
                Console.Write(output);
                return exitCode;
            },
            outcomeSelector: exitCode => SelectOutcome(
                exitCode,
                payload is not null && succeededSelector(payload),
                payload is not null && mutatedSelector(payload)),
            payloadSelector: _ => SerializePayload(payload, payloadTypeInfo),
            effectsSelector: _ => payload is null ? [] : effectsSelector(payload),
            ct: ct).ConfigureAwait(false);
    }

    internal static string WorkItemTarget(int workItemId) => $"workitem:{workItemId}";

    internal static JsonObject CreateAttributes(params (string Name, object? Value)[] attributes)
    {
        var result = new JsonObject();
        AppendAttributes(result, attributes);
        return result;
    }

    internal static void AppendAttributes(JsonObject attributes, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
        {
            if (value is null)
            {
                continue;
            }

            attributes[name] = JsonValue.Create(value);
        }
    }
}
