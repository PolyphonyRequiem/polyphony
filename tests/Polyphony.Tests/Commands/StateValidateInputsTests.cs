using System.Text.Json;
using Polyphony;
using Polyphony.Commands;
using Polyphony.Models;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony state validate-inputs</c> — the Move #2 Layer 3
/// preflight verb that substitutes for conductor's missing
/// <c>required: true</c> enforcement on workflow inputs.
/// </summary>
public sealed class StateValidateInputsTests
{
    private static StateCommands NewCmd() => new(
        twig: null!,
        git: null!,
        gh: null!,
        runner: null!,
        repository: null!,
        processConfig: null!);

    [Fact]
    public async Task ValidateInputs_MissingWorkflowYamlArg_EmitsHaltEnvelope()
    {
        var cmd = NewCmd();

        var (exitCode, output) = await CaptureAsync(() =>
            cmd.ValidateInputs(workflowYaml: null));

        exitCode.ShouldBe(ExitCodes.RoutingFailure);

        var halt = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        halt.ShouldNotBeNull();
        halt!.Action.ShouldBe("error");
        halt.Verb.ShouldBe("state validate-inputs");
        halt.MissingArgs.ShouldBe(["--workflow-yaml"]);
    }

    [Fact]
    public async Task ValidateInputs_AllRequiredSupplied_ReadyTrue()
    {
        var path = WriteWorkflow("""
        workflow:
          input:
            apex_item:
              required: true
            run_label:
              required: false
              default: actionable
        """);
        try
        {
            var cmd = NewCmd();

            var (exitCode, output) = await CaptureAsync(() =>
                cmd.ValidateInputs(workflowYaml: path, inputs: "apex_item=12345"));

            exitCode.ShouldBe(ExitCodes.Success);

            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.StateValidateInputsResult);
            result.ShouldNotBeNull();
            result!.Ready.ShouldBeTrue();
            result.Action.ShouldBe("ok");
            result.MissingRequiredInputs.ShouldBeEmpty();
            result.UnknownInputs.ShouldBeEmpty();
            result.Inputs.Count.ShouldBe(2);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidateInputs_MissingRequiredInput_ReadyFalseActionError()
    {
        var path = WriteWorkflow("""
        workflow:
          input:
            apex_item:
              required: true
            run_label:
              required: false
              default: actionable
        """);
        try
        {
            var cmd = NewCmd();

            var (exitCode, output) = await CaptureAsync(() =>
                cmd.ValidateInputs(workflowYaml: path, inputs: "run_label=foo"));

            exitCode.ShouldBe(ExitCodes.Success); // routing-style — always 0
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.StateValidateInputsResult);

            result.ShouldNotBeNull();
            result!.Ready.ShouldBeFalse();
            result.Action.ShouldBe("error");
            result.MissingRequiredInputs.ShouldBe(["apex_item"]);
            result.Summary.ShouldContain("apex_item");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidateInputs_UnknownInput_ReadyTrueButReported()
    {
        var path = WriteWorkflow("""
        workflow:
          input:
            apex_item:
              required: true
        """);
        try
        {
            var cmd = NewCmd();

            var (exitCode, output) = await CaptureAsync(() =>
                cmd.ValidateInputs(workflowYaml: path, inputs: "apex_item=12345,bogus_input=foo"));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.StateValidateInputsResult);

            result.ShouldNotBeNull();
            result!.Ready.ShouldBeTrue();
            result.UnknownInputs.ShouldBe(["bogus_input"]);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidateInputs_NoWorkflowInputSection_ReadyTrueEmptySchema()
    {
        var path = WriteWorkflow("""
        workflow:
          name: foo
        """);
        try
        {
            var cmd = NewCmd();

            var (exitCode, output) = await CaptureAsync(() =>
                cmd.ValidateInputs(workflowYaml: path, inputs: ""));

            exitCode.ShouldBe(ExitCodes.Success);
            var result = JsonSerializer.Deserialize(
                output, PolyphonyJsonContext.Default.StateValidateInputsResult);

            result.ShouldNotBeNull();
            result!.Ready.ShouldBeTrue();
            result.Inputs.ShouldBeEmpty();
            result.MissingRequiredInputs.ShouldBeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ValidateInputs_FileNotFound_ActionErrorWithReason()
    {
        var bogus = Path.Combine(Path.GetTempPath(), $"polyphony-validate-inputs-missing-{Guid.NewGuid():N}.yaml");

        var cmd = NewCmd();

        var (exitCode, output) = await CaptureAsync(() =>
            cmd.ValidateInputs(workflowYaml: bogus));

        exitCode.ShouldBe(ExitCodes.Success);
        var result = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.StateValidateInputsResult);

        result.ShouldNotBeNull();
        result!.Ready.ShouldBeFalse();
        result.Action.ShouldBe("error");
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not found");
    }

    [Fact]
    public void ParseSuppliedInputs_AcceptsCommaSemicolonNewlineSeparated()
    {
        var set = StateCommands.ParseSuppliedInputs("a=1,b=2;c=3\n d = 4 ");
        set.ShouldBe(new HashSet<string> { "a", "b", "c", "d" }, ignoreOrder: true);
    }

    [Fact]
    public void ParseSuppliedInputs_KeyOnlyEntryCounts()
    {
        var set = StateCommands.ParseSuppliedInputs("a,b=2");
        set.ShouldContain("a");
        set.ShouldContain("b");
    }

    [Fact]
    public void ParseSuppliedInputs_EmptyOrWhitespace_ReturnsEmpty()
    {
        StateCommands.ParseSuppliedInputs(null).ShouldBeEmpty();
        StateCommands.ParseSuppliedInputs("").ShouldBeEmpty();
        StateCommands.ParseSuppliedInputs("   ").ShouldBeEmpty();
    }

    private static string WriteWorkflow(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"polyphony-validate-inputs-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private static async Task<(int ExitCode, string Output)> CaptureAsync(Func<Task<int>> action)
    {
        await ConsoleTestLock.AsyncLock.WaitAsync();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var exitCode = await action();
                return (exitCode, writer.ToString().Trim());
            }
            finally { Console.SetOut(original); }
        }
        finally { ConsoleTestLock.AsyncLock.Release(); }
    }
}

