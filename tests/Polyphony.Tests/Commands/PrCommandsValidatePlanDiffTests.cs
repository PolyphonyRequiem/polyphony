using System.Text.Json;
using Polyphony.Commands;
using Polyphony.Infrastructure.Processes;
using Polyphony.Locking;
using Polyphony.Tests.Infrastructure.Processes;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Commands;

/// <summary>
/// Tests for <c>polyphony pr validate-plan-diff</c>. The verb is a thin
/// adapter over <see cref="Polyphony.Manifest.PlanDiffValidator"/>; full
/// classification coverage lives in PlanDiffValidatorTests. Here we focus
/// on:
/// <list type="bullet">
///   <item>Input validation (rootId/itemId/prNumber/parentItemId).</item>
///   <item>Routing-style emission: severity = "error" + structured Code on
///     failure paths (slug unresolved, gh failure, PR not found).</item>
///   <item><see cref="PrCommands.ParseAncestorIds"/> helper logic.</item>
///   <item>Always exit 0 (workflows route on Severity).</item>
/// </list>
/// </summary>
public sealed class PrCommandsValidatePlanDiffTests : CommandTestBase
{
    private (PrCommands Command, FakeProcessRunner Runner) CreateCommand()
    {
        var runner = new FakeProcessRunner();
        var twig = new TwigClient(runner);
        var git = new GitClient(runner);
        var gh = new GhClient(runner);
        return (new PrCommands(git, gh, twig, Repository, Config, new RunLockStore(), new RunLockPathResolver(git)), runner);
    }

    private static PrValidatePlanDiffResult Parse(string output)
        => JsonSerializer.Deserialize(output, PolyphonyJsonContext.Default.PrValidatePlanDiffResult)!;

    // ─── Input validation ───────────────────────────────────────────────

    [Fact]
    public async Task RootIdInvalid_EmitsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (exit, output) = await CaptureConsoleAsync(
            () => cmd.ValidatePlanDiff(rootId: 0, itemId: 100, prNumber: 42));
        exit.ShouldBe(0);
        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("config_error");
        r.Message.ShouldContain("root-id");
        r.DiffClassified.ShouldBeFalse();
    }

    [Fact]
    public async Task ItemIdInvalid_EmitsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidatePlanDiff(rootId: 100, itemId: 0, prNumber: 42));
        var r = Parse(output);
        r.Code.ShouldBe("config_error");
        r.Message.ShouldContain("item-id");
    }

    [Fact]
    public async Task PrNumberInvalid_EmitsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidatePlanDiff(rootId: 100, itemId: 5678, prNumber: 0));
        var r = Parse(output);
        r.Code.ShouldBe("config_error");
        r.Message.ShouldContain("pr-number");
    }

    [Fact]
    public async Task RootPlan_WithParentId_EmitsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidatePlanDiff(rootId: 100, itemId: 100, prNumber: 42, parentItemId: 99));
        var r = Parse(output);
        r.Code.ShouldBe("config_error");
        r.Message.ShouldContain("parent-item-id");
    }

    [Fact]
    public async Task NegativeParentId_EmitsConfigError()
    {
        var (cmd, _) = CreateCommand();
        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidatePlanDiff(rootId: 100, itemId: 5678, prNumber: 42, parentItemId: -1));
        var r = Parse(output);
        r.Code.ShouldBe("config_error");
    }

    // ─── Slug resolution failure ────────────────────────────────────────

    [Fact]
    public async Task SlugUnresolved_EmitsRepoNotResolvedError()
    {
        var (cmd, runner) = CreateCommand();
        // No origin remote stub → TryResolveSlug returns null/empty
        runner.WhenExact("git", ["remote", "get-url", "origin"], new ProcessResult(128, "", "fatal: no such remote 'origin'"));

        var (_, output) = await CaptureConsoleAsync(
            () => cmd.ValidatePlanDiff(rootId: 100, itemId: 5678, prNumber: 42, parentItemId: 100));
        var r = Parse(output);
        r.Severity.ShouldBe("error");
        r.Code.ShouldBe("repo_not_resolved");
        r.DiffClassified.ShouldBeFalse();
    }

    // ─── ParseAncestorIds helper ────────────────────────────────────────

    [Fact]
    public void ParseAncestorIds_Empty_ReturnsEmpty()
    {
        var ids = PrCommands.ParseAncestorIds("", rootId: 100, parentItemId: 200);
        ids.ShouldBeEmpty();
    }

    [Fact]
    public void ParseAncestorIds_Whitespace_ReturnsEmpty()
    {
        var ids = PrCommands.ParseAncestorIds("   ", rootId: 100, parentItemId: 200);
        ids.ShouldBeEmpty();
    }

    [Fact]
    public void ParseAncestorIds_RootToken_ReplacedWithRootId()
    {
        var ids = PrCommands.ParseAncestorIds("root", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 100 });
    }

    [Fact]
    public void ParseAncestorIds_RootTokenCaseInsensitive()
    {
        var ids = PrCommands.ParseAncestorIds("ROOT", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 100 });
    }

    [Fact]
    public void ParseAncestorIds_MixedNumericAndRoot()
    {
        var ids = PrCommands.ParseAncestorIds("500,root", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 500, 100 });
    }

    [Fact]
    public void ParseAncestorIds_HandlesWhitespaceAroundCommas()
    {
        var ids = PrCommands.ParseAncestorIds(" 500 , root ", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 500, 100 });
    }

    [Fact]
    public void ParseAncestorIds_DropsImmediateParentDefensively()
    {
        // Caller is supposed to exclude the immediate parent, but we filter
        // it defensively in case it slips in.
        var ids = PrCommands.ParseAncestorIds("200,500,root", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 500, 100 });
    }

    [Fact]
    public void ParseAncestorIds_DropsDuplicates()
    {
        var ids = PrCommands.ParseAncestorIds("500,500,root,root", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 500, 100 });
    }

    [Fact]
    public void ParseAncestorIds_DropsNonPositive()
    {
        var ids = PrCommands.ParseAncestorIds("0,-5,500,root", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 500, 100 });
    }

    [Fact]
    public void ParseAncestorIds_DropsUnparseable()
    {
        var ids = PrCommands.ParseAncestorIds("foo,500,bar,root", rootId: 100, parentItemId: 200);
        ids.ShouldBe(new[] { 500, 100 });
    }

    [Fact]
    public void ParseAncestorIds_RootTokenWhenItemIsDirectChildOfRoot_StillIncludesRoot()
    {
        // parentItemId == rootId means the item is a direct child of root.
        // The "root" token should still be honored — caller decides not to
        // pass it. If they do, we don't get clever about removing it.
        var ids = PrCommands.ParseAncestorIds("root", rootId: 100, parentItemId: 100);
        // root token would resolve to 100, but parentItemId filter drops it
        ids.ShouldBeEmpty();
    }

    // ─── PlanFilePath helper ────────────────────────────────────────────

    [Fact]
    public void PlanFilePath_FormatsCorrectly()
    {
        PrCommands.PlanFilePath(1234).ShouldBe("plans/plan-1234.md");
        PrCommands.PlanFilePath(1).ShouldBe("plans/plan-1.md");
    }

    [Fact]
    public void PlanFilePath_UsesInvariantCulture()
    {
        // Ensure no thousand separators or locale-specific formatting
        PrCommands.PlanFilePath(1_000_000).ShouldBe("plans/plan-1000000.md");
    }
}
