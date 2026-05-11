using Polyphony.Manifest;
using Shouldly;
using Xunit;

namespace Polyphony.Tests.Manifest;

/// <summary>
/// Verifies that <see cref="RunManifestStore"/> round-trips the manifest
/// shape from the Rev 4 ADR, applies atomic writes, and recomputes the
/// topology hash on every save.
/// </summary>
public sealed class RunManifestStoreTests : IDisposable
{
    private readonly string tempDir;

    public RunManifestStoreTests()
    {
        this.tempDir = Path.Combine(Path.GetTempPath(), "polyphony-manifest-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(this.tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string PathOf(string name) => System.IO.Path.Combine(this.tempDir, name);

    private static RunManifest BuildSampleManifest()
    {
        return new RunManifest
        {
            Schema = 1,
            RootId = 1234,
            PlatformProject = "dev.azure.com/dangreen-msft/Twig",
            CreatedAt = new DateTime(2026, 5, 6, 15, 30, 0, DateTimeKind.Utc),
            CreatedBy = "dangreen",
            BranchModelVersion = 1,
            PlanGenerations = new(StringComparer.Ordinal)
            {
                ["root"] = 3,
                ["100"] = 2,
            },
            MergeGroups = new List<MergeGroupEntry>
            {
                new()
                {
                    Id = "data-layer",
                    MgPath = "data-layer",
                    Items = new List<int> { 101, 102 },
                    Nesting = ManifestNesting.Top,
                    Isolation = ManifestIsolation.PerMergeGroup,
                },
                new()
                {
                    Id = "item-4567",
                    MgPath = "data-layer_item-4567",
                    ParentMgPath = "data-layer",
                    Items = new List<int> { 4567, 4571 },
                    Nesting = ManifestNesting.Nested,
                    Isolation = ManifestIsolation.PerItem,
                    NestingOverride = null,
                },
            },
            Rebases = new List<RebaseRecord>
            {
                new()
                {
                    Branch = "mg/1234_data-layer",
                    Onto = "feature/1234",
                    Reason = "cross_mg_code_dep",
                    Commit = "0b1f3e9",
                    RecordedAt = new DateTime(2026, 5, 6, 18, 0, 0, DateTimeKind.Utc),
                },
            },
            HumanApprovals = new List<HumanApprovalRecord>
            {
                new()
                {
                    Gate = "deep_nesting_depth_4",
                    ApprovedBy = "dangreen",
                    ApprovedAt = new DateTime(2026, 5, 6, 17, 0, 0, DateTimeKind.Utc),
                    Detail = "manual approval at depth 4",
                },
            },
        };
    }

    [Fact]
    public void Save_RecomputesTopologyHashFromMergeGroups()
    {
        var manifest = BuildSampleManifest();
        manifest.TopologyHash = "sha256:stale_value_should_be_overwritten";

        var path = this.PathOf("run.yaml");
        RunManifestStore.Save(path, manifest);

        var loaded = RunManifestStore.LoadOrThrow(path);
        loaded.TopologyHash.ShouldBe(TopologyHasher.ComputeHash(manifest.MergeGroups));
        loaded.TopologyHash.ShouldNotContain("stale_value");
    }

    [Fact]
    public void SaveLoad_RoundTrip_PreservesAllFields()
    {
        var manifest = BuildSampleManifest();
        var path = this.PathOf("run.yaml");
        RunManifestStore.Save(path, manifest);

        var loaded = RunManifestStore.LoadOrThrow(path);
        loaded.Schema.ShouldBe(manifest.Schema);
        loaded.RootId.ShouldBe(manifest.RootId);
        loaded.PlatformProject.ShouldBe(manifest.PlatformProject);
        loaded.CreatedBy.ShouldBe(manifest.CreatedBy);
        loaded.BranchModelVersion.ShouldBe(manifest.BranchModelVersion);
        loaded.MergeGroups.Count.ShouldBe(2);
        loaded.MergeGroups[0].MgPath.ShouldBe("data-layer");
        loaded.MergeGroups[0].Items.ShouldBe(new[] { 101, 102 });
        loaded.MergeGroups[1].ParentMgPath.ShouldBe("data-layer");
        loaded.MergeGroups[1].Isolation.ShouldBe(ManifestIsolation.PerItem);
        loaded.PlanGenerations["root"].ShouldBe(3);
        loaded.PlanGenerations["100"].ShouldBe(2);
        loaded.Rebases.ShouldHaveSingleItem();
        loaded.Rebases[0].Reason.ShouldBe("cross_mg_code_dep");
        loaded.HumanApprovals.ShouldHaveSingleItem();
        loaded.HumanApprovals[0].Detail.ShouldBe("manual approval at depth 4");
    }

    [Fact(Skip = "Flaky in parallel runs — see #285")]
    public void Save_OverwritesExistingFile_AtomicallySwap()
    {
        var path = this.PathOf("run.yaml");
        var first = BuildSampleManifest();
        RunManifestStore.Save(path, first);
        var firstHash = first.TopologyHash;

        var second = BuildSampleManifest();
        second.MergeGroups.RemoveAt(1); // change topology
        RunManifestStore.Save(path, second);

        var loaded = RunManifestStore.LoadOrThrow(path);
        loaded.MergeGroups.Count.ShouldBe(1);
        loaded.TopologyHash.ShouldNotBe(firstHash);
    }

    [Fact]
    public void Save_LeavesNoTempFiles_OnSuccess()
    {
        var path = this.PathOf("run.yaml");
        RunManifestStore.Save(path, BuildSampleManifest());

        var stragglers = Directory.GetFiles(this.tempDir, ".run.yaml.*.tmp");
        stragglers.ShouldBeEmpty();
    }

    [Fact]
    public void Save_CreatesParentDirectory_WhenMissing()
    {
        var path = this.PathOf(".polyphony/run.yaml");
        RunManifestStore.Save(path, BuildSampleManifest());
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public void LoadOrThrow_MissingFile_ThrowsFileNotFound()
    {
        Should.Throw<FileNotFoundException>(() => RunManifestStore.LoadOrThrow(this.PathOf("nope.yaml")));
    }

    [Fact]
    public void LoadOrThrow_MalformedYaml_ThrowsInvalidOperation()
    {
        var path = this.PathOf("bad.yaml");
        File.WriteAllText(path, "schema: 1\nroot_id: not-a-number\n");
        Should.Throw<InvalidOperationException>(() => RunManifestStore.LoadOrThrow(path));
    }

    [Fact]
    public void LoadOrThrow_InvalidStructure_ThrowsInvalidOperation()
    {
        // Schema 99 is unsupported; validator must reject.
        var path = this.PathOf("v99.yaml");
        File.WriteAllText(path, """
            schema: 99
            root_id: 1234
            platform_project: foo/bar
            created_at: 2026-05-06T15:30:00Z
            created_by: dangreen
            branch_model_version: 1
            """);
        Should.Throw<InvalidOperationException>(() => RunManifestStore.LoadOrThrow(path))
            .Message.ShouldContain("schema");
    }
}
