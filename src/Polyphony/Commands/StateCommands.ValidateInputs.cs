using System.Text.Json;
using ConsoleAppFramework;
using Polyphony.Annotations;
using YamlDotNet.RepresentationModel;

namespace Polyphony.Commands;

public sealed partial class StateCommands
{
    /// <summary>
    /// Validate that every <c>required: true</c> input declared in a workflow
    /// YAML's <c>workflow.input</c> schema is satisfied by the actual <c>--input</c>
    /// values passed at <c>conductor run</c>. Move #2 Layer 3 — the local
    /// workaround for issue #188 (conductor doesn't enforce
    /// <c>required: true</c>; without this preflight verb, a missing required
    /// input silently becomes a Jinja template error mid-run).
    /// </summary>
    /// <param name="workflowYaml">
    /// Absolute or workspace-relative path to the workflow YAML being run.
    /// Conductor passes this as <c>{{ workflow.file }}</c>.
    /// </param>
    /// <param name="inputs">
    /// Newline-, comma-, or semicolon-separated <c>key=value</c> pairs giving
    /// the inputs that were actually supplied at dispatch. Pass an empty
    /// string for "no inputs supplied" — the verb still runs, and reports
    /// every required input as missing. Values are not interpreted; presence
    /// vs absence is what matters.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Routing-style envelope: ALWAYS returns <see cref="ExitCodes.Success"/>.
    /// Consumers branch on the JSON payload's <c>ready</c> /
    /// <c>action == "error"</c> fields.
    /// </remarks>
    [Command("validate-inputs")]
    [VerbResult(typeof(StateValidateInputsResult))]
    public Task<int> ValidateInputs(
        string? workflowYaml = null,
        string? inputs = null,
        CancellationToken ct = default)
    {
        if (RequiredInput.HaltIfMissing("state validate-inputs",
            ("--workflow-yaml", string.IsNullOrWhiteSpace(workflowYaml))) is { } halt)
            return Task.FromResult(halt);

        StateValidateInputsResult result;
        try
        {
            var schema = ReadWorkflowInputSchema(workflowYaml!);
            var supplied = ParseSuppliedInputs(inputs);

            var diagnostics = new List<StateValidateInputsDiagnostic>(schema.Count);
            var missing = new List<string>();
            foreach (var (name, decl) in schema)
            {
                var present = supplied.Contains(name);
                string? reason = null;
                if (decl.Required && !present)
                {
                    reason = $"Required input '{name}' was not supplied via --input.";
                    missing.Add(name);
                }
                diagnostics.Add(new StateValidateInputsDiagnostic
                {
                    Name = name,
                    Required = decl.Required,
                    Supplied = present,
                    Default = decl.Default,
                    Reason = reason,
                });
            }

            var declaredNames = new HashSet<string>(schema.Select(kv => kv.Key), StringComparer.Ordinal);
            var unknown = supplied
                .Where(name => !declaredNames.Contains(name))
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            var ready = missing.Count == 0;
            var summary = ready
                ? unknown.Length == 0
                    ? "All required inputs supplied."
                    : $"All required inputs supplied. {unknown.Length} unknown input(s) ignored."
                : $"{missing.Count} required input(s) missing: {string.Join(", ", missing)}";

            result = new StateValidateInputsResult
            {
                Ready = ready,
                Summary = summary,
                Action = ready ? "ok" : "error",
                WorkflowYaml = workflowYaml!,
                Inputs = diagnostics,
                MissingRequiredInputs = [.. missing],
                UnknownInputs = unknown,
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result = new StateValidateInputsResult
            {
                Ready = false,
                Summary = $"Cannot validate inputs: {ex.Message}",
                Action = "error",
                WorkflowYaml = workflowYaml ?? "",
                Inputs = [],
                MissingRequiredInputs = [],
                UnknownInputs = [],
                Error = ex.Message,
            };
        }

        Console.WriteLine(JsonSerializer.Serialize(
            result, PolyphonyJsonContext.Default.StateValidateInputsResult));
        return Task.FromResult(ExitCodes.Success);
    }

    /// <summary>
    /// Reads the <c>workflow.input</c> schema from a conductor workflow YAML.
    /// Returns inputs in declaration order; <c>required</c> defaults to
    /// <c>false</c> when the key is absent (matching conductor's behaviour).
    /// </summary>
    internal static List<KeyValuePair<string, InputDecl>> ReadWorkflowInputSchema(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Workflow YAML not found: {path}", path);

        using var reader = new StreamReader(path);
        var stream = new YamlStream();
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            throw new InvalidOperationException("Workflow YAML is empty.");

        var root = stream.Documents[0].RootNode;
        if (root is not YamlMappingNode rootMap)
            throw new InvalidOperationException("Workflow YAML root is not a mapping.");

        // Schema discovered in the wild: top-level `workflow:` mapping with an
        // `input:` mapping (not `inputs:`). Fall back gracefully if the file
        // omits the section — return an empty schema so callers report "no
        // required inputs missing" rather than crashing.
        if (!rootMap.Children.TryGetValue("workflow", out var workflowNode)
            || workflowNode is not YamlMappingNode workflowMap)
            return [];

        if (!workflowMap.Children.TryGetValue("input", out var inputNode)
            || inputNode is not YamlMappingNode inputMap)
            return [];

        var result = new List<KeyValuePair<string, InputDecl>>(inputMap.Children.Count);
        foreach (var entry in inputMap.Children)
        {
            if (entry.Key is not YamlScalarNode { Value: { Length: > 0 } name }) continue;
            if (entry.Value is not YamlMappingNode declMap)
            {
                result.Add(new(name, new InputDecl(false, null)));
                continue;
            }

            bool required = false;
            string? @default = null;
            if (declMap.Children.TryGetValue("required", out var reqNode)
                && reqNode is YamlScalarNode reqScalar
                && bool.TryParse(reqScalar.Value, out var reqValue))
            {
                required = reqValue;
            }
            if (declMap.Children.TryGetValue("default", out var defNode)
                && defNode is YamlScalarNode defScalar)
            {
                @default = defScalar.Value;
            }
            result.Add(new(name, new InputDecl(required, @default)));
        }
        return result;
    }

    /// <summary>
    /// Parses the operator-supplied <c>--inputs</c> value into a set of
    /// supplied input names. Accepts <c>key=value</c> pairs separated by
    /// commas, semicolons, or newlines. Empty/whitespace input yields an
    /// empty set.
    /// </summary>
    internal static HashSet<string> ParseSuppliedInputs(string? raw)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw)) return result;

        var separators = new[] { ',', ';', '\n', '\r' };
        foreach (var token in raw.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            var name = eq < 0 ? token : token[..eq];
            name = name.Trim();
            if (name.Length > 0) result.Add(name);
        }
        return result;
    }

    internal readonly record struct InputDecl(bool Required, string? Default);
}
