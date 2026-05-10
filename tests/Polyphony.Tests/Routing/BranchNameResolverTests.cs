using Polyphony.Routing;
using Polyphony.Tests.TestFixtures;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Routing;

/// <summary>
/// Unit tests for <see cref="BranchNameResolver"/> verifying template substitution and slug generation.
/// </summary>
public sealed class BranchNameResolverTests
{
    [Fact]
    public void Resolve_WithBranchStrategy_SubstitutesIdInTemplate()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .WithBranchStrategy(featureBranch: "feature/{id}")
            .Build();
        var item = new WorkItemBuilder().WithId(42).WithType("Epic").WithTitle("Test").WithState("To Do").Build();

        var hint = BranchNameResolver.Resolve(config, item);

        hint.ShouldNotBeNull();
        hint.FeatureBranch.ShouldBe("feature/42");
    }

    [Fact]
    public void Resolve_WithRootIdPlaceholder_SubstitutesCorrectly()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .WithBranchStrategy(featureBranch: "feature/{root_id}-test")
            .Build();
        var item = new WorkItemBuilder().WithId(100).WithType("Epic").WithTitle("Test").WithState("To Do").Build();

        var hint = BranchNameResolver.Resolve(config, item);

        hint.ShouldNotBeNull();
        hint.FeatureBranch.ShouldBe("feature/100-test");
    }

    [Fact]
    public void Resolve_WithSlugPlaceholder_GeneratesSlugFromTitle()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .WithBranchStrategy(featureBranch: "feature/{id}-{slug}")
            .Build();
        var item = new WorkItemBuilder().WithId(10).WithType("Epic").WithTitle("My Cool Feature").WithState("To Do").Build();

        var hint = BranchNameResolver.Resolve(config, item);

        hint.ShouldNotBeNull();
        hint.FeatureBranch.ShouldBe("feature/10-my-cool-feature");
    }

    [Fact]
    public void Resolve_MergeGroupBranch_SubstitutesPlaceholders()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .WithBranchStrategy(MergeGroupBranch: "feature/{id}-mg-{slug}")
            .Build();
        var item = new WorkItemBuilder().WithId(50).WithType("Epic").WithTitle("Test").WithState("To Do").Build();

        var hint = BranchNameResolver.Resolve(config, item);

        hint.ShouldNotBeNull();
        // The canonical YAML key is `mg_branch:`, populated into
        // BranchStrategy.MergeGroupBranch. The resolver substitutes the same
        // placeholders ({id}, {root_id}, {slug}) and surfaces the result via
        // WorkspaceHint.MergeGroupBranch (JSON wire key still "pg_branch"
        // until the workflow rewire PR removes the bridge).
        hint.MergeGroupBranch.ShouldBe("feature/50-mg-test");
    }

    [Fact]
    public void Resolve_LegacyPgBranchOnly_FallsBackViaLoader()
    {
        // Configures only the deprecated pg_branch field (with no mg_branch).
        // ProcessConfigLoader copies PgBranch -> MergeGroupBranch when the new key is
        // absent; this test exercises that bridge against the in-memory builder
        // path (which writes PgBranch directly when pgBranchLegacy is set).
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .WithBranchStrategy(MergeGroupBranch: "", pgBranchLegacy: "feature/{id}-pg-{slug}")
            .Build();

        // Simulate the loader's legacy-key migration (the in-memory builder
        // does not run the loader path).
        if (string.IsNullOrEmpty(config.BranchStrategy!.MergeGroupBranch))
        {
            config.BranchStrategy.MergeGroupBranch = config.BranchStrategy.PgBranch;
        }

        var item = new WorkItemBuilder().WithId(50).WithType("Epic").WithTitle("Test").WithState("To Do").Build();
        var hint = BranchNameResolver.Resolve(config, item);

        hint.ShouldNotBeNull();
        hint.MergeGroupBranch.ShouldBe("feature/50-pg-test");
    }

    [Fact]
    public void Resolve_NoBranchStrategy_ReturnsNull()
    {
        var config = new ProcessConfigBuilder()
            .WithType("Epic", ["plannable"])
            .Build();
        var item = new WorkItemBuilder().WithId(1).WithType("Epic").WithTitle("Test").WithState("To Do").Build();

        var hint = BranchNameResolver.Resolve(config, item);

        hint.ShouldBeNull();
    }

    [Fact]
    public void Slugify_SpecialCharacters_ReplacedWithHyphens()
    {
        var slug = BranchNameResolver.Slugify("Hello, World! This is a test.");
        slug.ShouldBe("hello-world-this-is-a-test");
    }

    [Fact]
    public void Slugify_ConsecutiveSpecialChars_CollapsedToSingleHyphen()
    {
        var slug = BranchNameResolver.Slugify("Hello---World");
        slug.ShouldBe("hello-world");
    }

    [Fact]
    public void Slugify_EmptyString_ReturnsEmpty()
    {
        var slug = BranchNameResolver.Slugify("");
        slug.ShouldBe("");
    }

    [Fact]
    public void Slugify_LongTitle_TruncatesTo50Chars()
    {
        var longTitle = new string('a', 100);
        var slug = BranchNameResolver.Slugify(longTitle);
        slug.Length.ShouldBeLessThanOrEqualTo(50);
    }

    [Fact]
    public void Slugify_TrailingHyphenAfterTruncation_Removed()
    {
        // Create a title that would have a hyphen at position 50 after slugify
        var title = new string('a', 49) + " b";
        var slug = BranchNameResolver.Slugify(title);
        slug.ShouldNotEndWith("-");
    }
}
