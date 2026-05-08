using System.Text.Json;
using Polyphony;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Unit tests for the Move #2 <see cref="RequiredInput"/> helper. Covers the
/// sentinel value, the halt-emit-and-exit contract, and the JSON envelope
/// shape that conductor's routing branches will see.
/// </summary>
public sealed class RequiredInputTests
{
    [Fact]
    public void MissingInt_IsIntMinValue()
    {
        // The sentinel must be a value no plausible workflow input could ever pass —
        // anything in the small-positive range is reachable by a real arg.
        RequiredInput.MissingInt.ShouldBe(int.MinValue);
    }

    [Fact]
    public void HaltIfMissing_AllPresent_ReturnsNull()
    {
        var (_, output) = CaptureStdout(() =>
            RequiredInput.HaltIfMissing("branch ensure-plan",
                ("--root-id", false),
                ("--item-id", false)));

        output.ShouldBe(string.Empty);
    }

    [Fact]
    public void HaltIfMissing_OneMissing_EmitsEnvelopeAndReturnsRoutingFailure()
    {
        var (result, output) = CaptureStdout(() =>
            RequiredInput.HaltIfMissing("branch ensure-plan",
                ("--root-id", true),
                ("--item-id", false)));

        result.ShouldBe(ExitCodes.RoutingFailure);

        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);

        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("branch ensure-plan");
        envelope.Error.ShouldContain("--root-id");
        envelope.MissingArgs.ShouldBe(["--root-id"]);
    }

    [Fact]
    public void HaltIfMissing_MultipleMissing_ListsAllInOrder()
    {
        var (result, output) = CaptureStdout(() =>
            RequiredInput.HaltIfMissing("manifest init",
                ("--root-id", true),
                ("--platform-project", true)));

        result.ShouldBe(ExitCodes.RoutingFailure);

        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);

        envelope.ShouldNotBeNull();
        envelope!.MissingArgs.ShouldBe(["--root-id", "--platform-project"]);
        envelope.Error.ShouldContain("manifest init");
        envelope.Error.ShouldContain("--root-id, --platform-project");
    }

    [Fact]
    public void HaltIfMissing_EmittedEnvelopeUsesSnakeCase()
    {
        var (_, output) = CaptureStdout(() =>
            RequiredInput.HaltIfMissing("plan write-plan",
                ("--item-id", true)));

        output.ShouldContain("\"missing_args\"");
        output.ShouldNotContain("\"missingArgs\"");
        output.ShouldContain("\"action\"");
        output.ShouldContain("\"verb\"");
    }

    [Fact]
    public void EmitDispatchErrorEnvelope_NoMissingArgs_OmitsArrayIsEmpty()
    {
        var (_, output) = CaptureStdout(() =>
        {
            RequiredInput.EmitDispatchErrorEnvelope(
                verb: "",
                error: "Unknown verb 'foo'.");
            return 0;
        });

        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);

        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe(string.Empty);
        envelope.Error.ShouldBe("Unknown verb 'foo'.");
        envelope.MissingArgs.ShouldBeEmpty();
    }

    [Fact]
    public void EmitDispatchErrorEnvelope_WithMissingArgs_PreservesList()
    {
        var (_, output) = CaptureStdout(() =>
        {
            RequiredInput.EmitDispatchErrorEnvelope(
                verb: "branch ensure-plan",
                error: "missing args",
                missingArgs: ["--root-id"]);
            return 0;
        });

        var envelope = JsonSerializer.Deserialize(
            output, PolyphonyJsonContext.Default.RequiredInputErrorResult);

        envelope!.MissingArgs.ShouldBe(["--root-id"]);
    }

    private static (T Result, string Output) CaptureStdout<T>(Func<T> action)
    {
        ConsoleTestLock.AsyncLock.Wait();
        try
        {
            using var writer = new StringWriter();
            var original = Console.Out;
            Console.SetOut(writer);
            try
            {
                var r = action();
                return (r, writer.ToString().Trim());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }
    }
}
