using Polyphony;
using Polyphony.Tests.Commands;
using Shouldly;
using Xunit;
using System.Text.Json;

namespace Polyphony.Tests;

/// <summary>
/// Unit tests for the <see cref="StringBoolArg"/> shim. The shim exists
/// because ConsoleAppFramework treats a <see cref="bool"/> parameter as
/// a no-value-only switch and rejects every explicit-value form
/// (<c>--flag true</c>, <c>--flag=true</c>, <c>--flag:true</c>) with
/// <c>"Argument 'true' is not recognized."</c>. Verbs that workflow YAMLs
/// pass an explicit value to must declare the param as <see cref="string"/>
/// and run it through this shim. See PR #451 (first instance:
/// <c>--allow-any-approval-vote</c>), PR for AB#3211 (second instance:
/// <c>--delete-branch false</c>), and the lint at
/// <c>tests/lint-caf-bool-value-form.ps1</c>.
/// </summary>
public sealed class StringBoolArgTests
{
    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData("tRuE")]
    public void Parse_AcceptsTrueCaseInsensitive(string raw)
    {
        var parsed = StringBoolArg.Parse("test-verb", "--flag", raw);
        parsed.ShouldBe(true);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    [InlineData("fAlSe")]
    public void Parse_AcceptsFalseCaseInsensitive(string raw)
    {
        var parsed = StringBoolArg.Parse("test-verb", "--flag", raw);
        parsed.ShouldBe(false);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  true  ")]   // whitespace is NOT trimmed — workflows should omit the flag if they want default
    [InlineData("yes")]
    [InlineData("no")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("garbage")]
    [InlineData("trueish")]
    public void Parse_RejectsAnythingElse_EmitsEnvelope(string raw)
    {
        // Console.Out is process-global; tests across classes can run in
        // parallel. Serialize against every other Console-redirecting test
        // via the shared lock — see ConsoleTestLock.cs for the convention.
        ConsoleTestLock.AsyncLock.Wait();
        bool? parsed;
        string stdout;
        try
        {
            var sw = new StringWriter();
            var origOut = Console.Out;
            Console.SetOut(sw);
            try { parsed = StringBoolArg.Parse("pr merge-impl-pr", "--delete-branch", raw); }
            finally { Console.SetOut(origOut); }
            stdout = sw.ToString().Trim();
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }

        parsed.ShouldBeNull();
        stdout.ShouldNotBeEmpty();

        var envelope = JsonSerializer.Deserialize(
            stdout, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope.ShouldNotBeNull();
        envelope!.Action.ShouldBe("error");
        envelope.Verb.ShouldBe("pr merge-impl-pr");
        envelope.Error.ShouldContain("--delete-branch");
        envelope.Error.ShouldContain("'true' or 'false'");
        envelope.MissingArgs.ShouldContain("--delete-branch");
    }

    [Fact]
    public void Parse_VerbAndFlagAppearInEnvelopeVerbatim()
    {
        // The envelope's `verb` field is consumed by conductor's event log
        // to attribute the failure to a node; the flag in `missing_args`
        // lets downstream remediation route on the specific flag.
        ConsoleTestLock.AsyncLock.Wait();
        string stdout;
        try
        {
            var sw = new StringWriter();
            var origOut = Console.Out;
            Console.SetOut(sw);
            try { StringBoolArg.Parse("pr poll-status-ado", "--allow-any-approval-vote", "maybe"); }
            finally { Console.SetOut(origOut); }
            stdout = sw.ToString().Trim();
        }
        finally
        {
            ConsoleTestLock.AsyncLock.Release();
        }

        var envelope = JsonSerializer.Deserialize(
            stdout, PolyphonyJsonContext.Default.RequiredInputErrorResult);
        envelope!.Verb.ShouldBe("pr poll-status-ado");
        envelope.MissingArgs.ShouldContain("--allow-any-approval-vote");
    }
}
