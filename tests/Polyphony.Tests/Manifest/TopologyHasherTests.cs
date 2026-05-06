using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Tests the topology hash canonicalization rules from the Rev 4
/// branch-model ADR (<c>docs/decisions/branch-model.md</c> § Topology
/// hash inputs). The hash must be deterministic, exclude
/// non-topological fields, and respect ordinal sorting + null
/// canonicalization.
/// </summary>
public sealed class TopologyHasherTests
{
    /// <summary>
    /// Pinned constant: SHA-256 of empty UTF-8 text. The empty MG set
    /// must produce this exact value — proves we hash the empty
    /// canonical text (not "null" or some other sentinel).
    /// </summary>
    private const string EmptyHash =
        "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void ComputeHash_EmptyMergeGroups_ReturnsHashOfEmptyText()
    {
        var hash = TopologyHasher.ComputeHash(Array.Empty<MergeGroupEntry>());
        hash.ShouldBe(EmptyHash);
    }

    [Fact]
    public void ComputeHash_SingleMergeGroup_ProducesStableValue()
    {
        var entry = new MergeGroupEntry
        {
            Id = "data-layer",
            MgPath = "data-layer",
            ParentMgPath = null,
            Items = new List<int> { 102, 101 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
            NestingOverride = null,
        };

        var canonical = TopologyHasher.BuildCanonicalText(new[] { entry });
        // Items must be sorted ascending; nesting_override null becomes "null".
        canonical.ShouldBe("data-layer\t101,102\tper-merge-group\tnull\n");

        var hash = TopologyHasher.ComputeHash(new[] { entry });
        hash.ShouldStartWith("sha256:");
        hash.Length.ShouldBe("sha256:".Length + 64);
    }

    [Fact]
    public void ComputeHash_RecordsSortedByMgPath_OrdinalLexicographic()
    {
        var ui = new MergeGroupEntry
        {
            Id = "ui-surface",
            MgPath = "ui-surface",
            Items = new List<int> { 200 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
        };
        var data = new MergeGroupEntry
        {
            Id = "data-layer",
            MgPath = "data-layer",
            Items = new List<int> { 101 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
        };

        var canonicalForward = TopologyHasher.BuildCanonicalText(new[] { ui, data });
        var canonicalReversed = TopologyHasher.BuildCanonicalText(new[] { data, ui });
        canonicalForward.ShouldBe(canonicalReversed);
        canonicalForward.ShouldBe("data-layer\t101\tper-merge-group\tnull\nui-surface\t200\tper-merge-group\tnull\n");
    }

    [Fact]
    public void ComputeHash_HyphenatedIsolation_PreservedExactly()
    {
        var entry = new MergeGroupEntry
        {
            Id = "leaf",
            MgPath = "leaf",
            Items = new List<int> { 1 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerItem,
        };

        TopologyHasher.BuildCanonicalText(new[] { entry })
            .ShouldContain("\tper-item\t");
    }

    [Fact]
    public void ComputeHash_NullOverride_CanonicalizesAsLiteralNull()
    {
        var entry = new MergeGroupEntry
        {
            Id = "leaf",
            MgPath = "leaf",
            Items = new List<int> { 1 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
            NestingOverride = null,
        };

        TopologyHasher.BuildCanonicalText(new[] { entry })
            .ShouldEndWith("\tnull\n");
    }

    [Fact]
    public void ComputeHash_FlatOverride_PreservedAsLiteralFlat()
    {
        var entry = new MergeGroupEntry
        {
            Id = "leaf",
            MgPath = "leaf",
            Items = new List<int> { 1 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
            NestingOverride = ManifestOverride.Flat,
        };

        TopologyHasher.BuildCanonicalText(new[] { entry })
            .ShouldEndWith("\tflat\n");
    }

    [Fact]
    public void ComputeHash_NamedOverride_PreservedExactly()
    {
        var entry = new MergeGroupEntry
        {
            Id = "leaf",
            MgPath = "leaf",
            Items = new List<int> { 1 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerMergeGroup,
            NestingOverride = "data-migrations",
        };

        TopologyHasher.BuildCanonicalText(new[] { entry })
            .ShouldEndWith("\tdata-migrations\n");
    }

    [Fact]
    public void ComputeHash_DistinguishesSameTerminalUnderDifferentParents()
    {
        // ADR motivation: two MGs with the same terminal id under
        // different parents must produce different topology hashes.
        var aLeaf = new MergeGroupEntry
        {
            Id = "leaf",
            MgPath = "alpha_leaf",
            ParentMgPath = "alpha",
            Items = new List<int> { 1 },
            Nesting = ManifestNesting.Nested,
            Isolation = ManifestIsolation.PerMergeGroup,
        };
        var bLeaf = new MergeGroupEntry
        {
            Id = "leaf",
            MgPath = "beta_leaf",
            ParentMgPath = "beta",
            Items = new List<int> { 1 },
            Nesting = ManifestNesting.Nested,
            Isolation = ManifestIsolation.PerMergeGroup,
        };

        var hashA = TopologyHasher.ComputeHash(new[] { aLeaf });
        var hashB = TopologyHasher.ComputeHash(new[] { bLeaf });
        hashA.ShouldNotBe(hashB);
    }

    [Fact]
    public void ComputeHash_DeterministicBetweenInvocations()
    {
        var mgs = new[]
        {
            new MergeGroupEntry
            {
                Id = "core",
                MgPath = "core",
                Items = new List<int> { 1, 2 },
                Nesting = ManifestNesting.Top,
                Isolation = ManifestIsolation.PerMergeGroup,
            },
        };

        var first = TopologyHasher.ComputeHash(mgs);
        var second = TopologyHasher.ComputeHash(mgs);
        first.ShouldBe(second);
    }

    [Fact]
    public void ComputeHash_NullArgument_Throws()
    {
        Should.Throw<ArgumentNullException>(() => TopologyHasher.ComputeHash(null!));
    }

    [Fact]
    public void ComputeHash_ItemReordering_DoesNotChangeHash()
    {
        var make = () => new MergeGroupEntry
        {
            Id = "core",
            MgPath = "core",
            Items = new List<int> { 5, 1, 3 },
            Nesting = ManifestNesting.Top,
            Isolation = ManifestIsolation.PerItem,
            NestingOverride = "alt-name",
        };

        var a = TopologyHasher.ComputeHash(new[] { make() });
        var b = TopologyHasher.ComputeHash(new[] { make() });
        a.ShouldBe(b);

        var reordered = make();
        reordered.Items = new List<int> { 3, 5, 1 };
        TopologyHasher.ComputeHash(new[] { reordered }).ShouldBe(a);
    }
}
